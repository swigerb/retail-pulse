using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using RetailPulse.Contracts;

namespace RetailPulse.McpServer.Data;

/// <summary>
/// SQLite-backed data store for retail metrics. Seeds deterministically from tenant.yaml
/// on first run, then preserves mutations across restarts.
/// </summary>
public class RetailPulseDb
{
    private readonly string _connectionString;
    private readonly TenantConfiguration _tenant;
    private readonly string _tenantConfigPath;

    public RetailPulseDb(ITenantProvider tenantProvider, string dbPath, string tenantConfigPath)
    {
        _tenant = tenantProvider.GetTenant();
        _tenantConfigPath = tenantConfigPath;

        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        InitializeSchema();
        SeedIfNeeded();
    }

    // ── Schema ───────────────────────────────────────────────────────────

    private void InitializeSchema()
    {
        using var conn = OpenConnection();
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;

            CREATE TABLE IF NOT EXISTS Depletions (
                Brand TEXT NOT NULL COLLATE NOCASE,
                Region TEXT NOT NULL COLLATE NOCASE,
                DepletionsYoY TEXT NOT NULL,
                SellThroughYoY TEXT NOT NULL,
                InventoryWeeks REAL NOT NULL,
                Status TEXT NOT NULL,
                SentimentSummary TEXT NOT NULL,
                PRIMARY KEY (Brand, Region)
            );

            CREATE TABLE IF NOT EXISTS Shipments (
                Brand TEXT NOT NULL COLLATE NOCASE,
                Region TEXT NOT NULL COLLATE NOCASE,
                ShipmentsYoY TEXT NOT NULL,
                SellThroughYoY TEXT NOT NULL,
                DepletionsYoY TEXT NOT NULL,
                InventoryWeeks REAL NOT NULL,
                CasesShipped INTEGER NOT NULL,
                CasesDepleted INTEGER NOT NULL,
                AnomalyType TEXT NOT NULL,
                RiskLevel TEXT NOT NULL,
                Analysis TEXT NOT NULL,
                PRIMARY KEY (Brand, Region)
            );

            CREATE TABLE IF NOT EXISTS Sentiment (
                Brand TEXT NOT NULL COLLATE NOCASE,
                Region TEXT NOT NULL COLLATE NOCASE,
                Sentiment TEXT NOT NULL,
                PRIMARY KEY (Brand, Region)
            );

            CREATE TABLE IF NOT EXISTS VariantMix (
                Brand TEXT NOT NULL COLLATE NOCASE,
                Region TEXT NOT NULL COLLATE NOCASE,
                Variant TEXT NOT NULL COLLATE NOCASE,
                MixPercent REAL NOT NULL,
                DepletionsYoY REAL NOT NULL,
                PRIMARY KEY (Brand, Region, Variant)
            );

            CREATE TABLE IF NOT EXISTS SeedMetadata (
                Key TEXT PRIMARY KEY,
                Value TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    // ── Seeding ──────────────────────────────────────────────────────────

    private void SeedIfNeeded()
    {
        var currentHash = ComputeTenantHash();

        using var conn = OpenConnection();
        conn.Open();

        // Check stored hash
        using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = "SELECT Value FROM SeedMetadata WHERE Key = 'tenant_hash'";
        var storedHash = checkCmd.ExecuteScalar() as string;

        if (storedHash == currentHash)
            return; // DB is current, preserve mutations

        // Re-seed: clear and regenerate
        using var tx = conn.BeginTransaction();

        using var clearCmd = conn.CreateCommand();
        clearCmd.CommandText = "DELETE FROM Depletions; DELETE FROM Shipments; DELETE FROM Sentiment; DELETE FROM VariantMix; DELETE FROM SeedMetadata;";
        clearCmd.ExecuteNonQuery();

        SeedDepletions(conn);
        SeedShipments(conn);
        SeedSentiment(conn);
        SeedVariantMix(conn);

        // Store hash
        using var hashCmd = conn.CreateCommand();
        hashCmd.CommandText = "INSERT INTO SeedMetadata (Key, Value) VALUES ('tenant_hash', @hash)";
        hashCmd.Parameters.AddWithValue("@hash", currentHash);
        hashCmd.ExecuteNonQuery();

        tx.Commit();
    }

    // Bump this version whenever the schema or seeding logic changes
    // to force a re-seed even if tenant.yaml hasn't changed.
    private const int SchemaVersion = 2;

    private string ComputeTenantHash()
    {
        if (!File.Exists(_tenantConfigPath))
            return "no-file";

        var bytes = File.ReadAllBytes(_tenantConfigPath);
        var hash = SHA256.HashData(bytes);
        return $"v{SchemaVersion}:{Convert.ToHexStringLower(hash)}";
    }

    private void SeedDepletions(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Depletions (Brand, Region, DepletionsYoY, SellThroughYoY, InventoryWeeks, Status, SentimentSummary)
            VALUES (@brand, @region, @dep, @sell, @inv, @status, @summary)
            """;

        var pBrand = cmd.Parameters.Add("@brand", SqliteType.Text);
        var pRegion = cmd.Parameters.Add("@region", SqliteType.Text);
        var pDep = cmd.Parameters.Add("@dep", SqliteType.Text);
        var pSell = cmd.Parameters.Add("@sell", SqliteType.Text);
        var pInv = cmd.Parameters.Add("@inv", SqliteType.Real);
        var pStatus = cmd.Parameters.Add("@status", SqliteType.Text);
        var pSummary = cmd.Parameters.Add("@summary", SqliteType.Text);

        foreach (var brand in _tenant.Brands)
        {
            var brandSeed = GetStableHash(brand.Name);
            var baseTrend = GetBaseTrend(brand, brandSeed);

            foreach (var region in _tenant.Regions)
            {
                var regionSeed = GetStableHash($"{brand.Name}|{region}");
                var regionRng = new Random(regionSeed);

                var regionVariance = (regionRng.NextDouble() - 0.5) * 8.0;
                var depletionGrowth = Math.Round(baseTrend + regionVariance, 1);
                var sellThroughGrowth = Math.Round(depletionGrowth + (regionRng.NextDouble() - 0.5) * 4.0, 1);
                var inventoryWeeks = Math.Round(Math.Max(2.5, 7.0 - depletionGrowth * 0.3 + regionRng.NextDouble() * 3.0), 1);
                var status = DetermineDepletionStatus(depletionGrowth, sellThroughGrowth, inventoryWeeks);
                var summary = GenerateDepletionSummary(brand, region, depletionGrowth, sellThroughGrowth, inventoryWeeks, status, regionRng);

                pBrand.Value = brand.Name;
                pRegion.Value = region;
                pDep.Value = FormatPercentage(depletionGrowth);
                pSell.Value = FormatPercentage(sellThroughGrowth);
                pInv.Value = inventoryWeeks;
                pStatus.Value = status;
                pSummary.Value = summary;
                cmd.ExecuteNonQuery();
            }
        }
    }

    private void SeedShipments(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Shipments (Brand, Region, ShipmentsYoY, SellThroughYoY, DepletionsYoY, InventoryWeeks, CasesShipped, CasesDepleted, AnomalyType, RiskLevel, Analysis)
            VALUES (@brand, @region, @ship, @sell, @dep, @inv, @casesShip, @casesDep, @anomaly, @risk, @analysis)
            """;

        var pBrand = cmd.Parameters.Add("@brand", SqliteType.Text);
        var pRegion = cmd.Parameters.Add("@region", SqliteType.Text);
        var pShip = cmd.Parameters.Add("@ship", SqliteType.Text);
        var pSell = cmd.Parameters.Add("@sell", SqliteType.Text);
        var pDep = cmd.Parameters.Add("@dep", SqliteType.Text);
        var pInv = cmd.Parameters.Add("@inv", SqliteType.Real);
        var pCasesShip = cmd.Parameters.Add("@casesShip", SqliteType.Integer);
        var pCasesDep = cmd.Parameters.Add("@casesDep", SqliteType.Integer);
        var pAnomaly = cmd.Parameters.Add("@anomaly", SqliteType.Text);
        var pRisk = cmd.Parameters.Add("@risk", SqliteType.Text);
        var pAnalysis = cmd.Parameters.Add("@analysis", SqliteType.Text);

        foreach (var brand in _tenant.Brands)
        {
            var baseTrend = GetBaseTrend(brand, GetStableHash(brand.Name));

            foreach (var region in _tenant.Regions)
            {
                var regionSeed = GetStableHash($"ship|{brand.Name}|{region}");
                var regionRng = new Random(regionSeed);

                var shipmentGrowth = Math.Round(baseTrend + (regionRng.NextDouble() - 0.3) * 6.0, 1);
                var sellThroughGrowth = Math.Round(baseTrend + (regionRng.NextDouble() - 0.5) * 5.0, 1);
                var depletionGrowth = Math.Round(sellThroughGrowth + (regionRng.NextDouble() - 0.5) * 3.0, 1);
                var inventoryWeeks = Math.Round(Math.Max(2.5, 6.5 - depletionGrowth * 0.25 + regionRng.NextDouble() * 3.0), 1);

                var baseCases = brand.PriceSegment switch
                {
                    "Ultra-Premium" => 2_000 + regionRng.Next(1_000, 5_000),
                    "Premium" => 5_000 + regionRng.Next(3_000, 15_000),
                    _ => 8_000 + regionRng.Next(5_000, 25_000)
                };
                var casesShipped = baseCases + (int)(baseCases * shipmentGrowth / 100.0);
                var casesDepleted = baseCases + (int)(baseCases * depletionGrowth / 100.0);

                var (anomalyType, riskLevel) = DetermineAnomalyType(shipmentGrowth, sellThroughGrowth, depletionGrowth, inventoryWeeks);
                var analysis = GenerateShipmentAnalysis(brand, region, shipmentGrowth, sellThroughGrowth, depletionGrowth, inventoryWeeks, casesShipped, casesDepleted, anomalyType);

                pBrand.Value = brand.Name;
                pRegion.Value = region;
                pShip.Value = FormatPercentage(shipmentGrowth);
                pSell.Value = FormatPercentage(sellThroughGrowth);
                pDep.Value = FormatPercentage(depletionGrowth);
                pInv.Value = inventoryWeeks;
                pCasesShip.Value = casesShipped;
                pCasesDep.Value = casesDepleted;
                pAnomaly.Value = anomalyType;
                pRisk.Value = riskLevel;
                pAnalysis.Value = analysis;
                cmd.ExecuteNonQuery();
            }
        }
    }

    private void SeedSentiment(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Sentiment (Brand, Region, Sentiment)
            VALUES (@brand, @region, @sentiment)
            """;

        var pBrand = cmd.Parameters.Add("@brand", SqliteType.Text);
        var pRegion = cmd.Parameters.Add("@region", SqliteType.Text);
        var pSentiment = cmd.Parameters.Add("@sentiment", SqliteType.Text);

        foreach (var brand in _tenant.Brands)
        {
            var baseTrend = GetBaseTrend(brand, GetStableHash(brand.Name));

            foreach (var region in _tenant.Regions)
            {
                var regionSeed = GetStableHash($"sent|{brand.Name}|{region}");
                var regionRng = new Random(regionSeed);

                var sentiment = GenerateFieldSentiment(brand, region, baseTrend, regionRng);

                pBrand.Value = brand.Name;
                pRegion.Value = region;
                pSentiment.Value = sentiment;
                cmd.ExecuteNonQuery();
            }
        }
    }

    private void SeedVariantMix(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO VariantMix (Brand, Region, Variant, MixPercent, DepletionsYoY)
            VALUES (@brand, @region, @variant, @mix, @dep)
            """;

        var pBrand = cmd.Parameters.Add("@brand", SqliteType.Text);
        var pRegion = cmd.Parameters.Add("@region", SqliteType.Text);
        var pVariant = cmd.Parameters.Add("@variant", SqliteType.Text);
        var pMix = cmd.Parameters.Add("@mix", SqliteType.Real);
        var pDep = cmd.Parameters.Add("@dep", SqliteType.Real);

        foreach (var brand in _tenant.Brands)
        {
            if (brand.Variants.Count == 0) continue;

            foreach (var region in _tenant.Regions)
            {
                var seed = GetStableHash($"variant|{brand.Name}|{region}");
                var rng = new Random(seed);

                // Generate weights in [0.5, 2.0], normalize to 100% mix
                var weights = brand.Variants.Select(_ => 0.5 + rng.NextDouble() * 1.5).ToArray();
                var total = weights.Sum();

                for (int i = 0; i < brand.Variants.Count; i++)
                {
                    var mixPct = Math.Round(weights[i] / total * 100.0, 1);
                    var depYoY = Math.Round((rng.NextDouble() - 0.5) * 10.0, 1); // ±5%

                    pBrand.Value = brand.Name;
                    pRegion.Value = region;
                    pVariant.Value = brand.Variants[i];
                    pMix.Value = mixPct;
                    pDep.Value = depYoY;
                    cmd.ExecuteNonQuery();
                }
            }
        }
    }

    // ── Query Methods (public API — matches SimulatedMetricsData surface) ─

    public object GetDepletionStats(string brand, string region, string period)
    {
        if (string.IsNullOrWhiteSpace(brand))
            return new { error = "Parameter 'brand' is required.", available_brands = GetAvailableBrands() };
        if (string.IsNullOrWhiteSpace(region))
            return new { error = "Parameter 'region' is required.", available_regions = GetAvailableRegions() };

        if (region.Trim().Equals("National", StringComparison.OrdinalIgnoreCase) ||
            region.Trim().Equals("All", StringComparison.OrdinalIgnoreCase))
        {
            return GetNationalDepletionStats(brand.Trim(), period);
        }

        using var conn = OpenConnection();
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Brand, Region, DepletionsYoY, SellThroughYoY, InventoryWeeks, Status, SentimentSummary
            FROM Depletions
            WHERE Brand LIKE @brand AND Region LIKE @region
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@brand", $"%{brand.Trim()}%");
        cmd.Parameters.AddWithValue("@region", $"%{region.Trim()}%");

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return new { error = $"No data found for brand '{brand}' in region '{region}'.", available_brands = GetAvailableBrands(), available_regions = GetAvailableRegions() };

        var actualBrand = reader.GetString(0);
        var actualRegion = reader.GetString(1);
        var depYoY = reader.GetString(2);
        var sellYoY = reader.GetString(3);
        var invWeeks = reader.GetDouble(4);
        var status = reader.GetString(5);
        var summary = reader.GetString(6);

        var periodMultiplier = GetPeriodMultiplier(period);

        return new
        {
            brand = actualBrand,
            region = actualRegion,
            period = period ?? "YTD",
            metrics = new
            {
                depletions_yoy = AdjustPercentage(depYoY, periodMultiplier),
                sell_through_yoy = AdjustPercentage(sellYoY, periodMultiplier),
                inventory_weeks_on_hand = Math.Round(invWeeks * (2.0 - periodMultiplier), 1),
                status
            },
            sentiment_summary = summary
        };
    }

    public object GetPortfolioDepletionStats(string region, string period)
    {
        if (string.IsNullOrWhiteSpace(region))
            return new { error = "Parameter 'region' is required.", available_regions = GetAvailableRegions() };

        var normalizedRegion = region.Trim();
        var normalizedPeriod = string.IsNullOrWhiteSpace(period) ? "YTD" : period.Trim();

        var results = new List<object>();
        foreach (var brand in _tenant.Brands)
        {
            results.Add(GetDepletionStats(brand.Name, normalizedRegion, normalizedPeriod));
        }

        return new
        {
            brands = results,
            region = normalizedRegion,
            period = normalizedPeriod,
            brandCount = results.Count
        };
    }

    public object GetShipmentStats(string brand, string region, string period)
    {
        if (string.IsNullOrWhiteSpace(brand))
            return new { error = "Parameter 'brand' is required.", available_brands = GetAvailableBrands() };
        if (string.IsNullOrWhiteSpace(region))
            return new { error = "Parameter 'region' is required.", available_regions = GetAvailableRegions() };

        using var conn = OpenConnection();
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Brand, Region, ShipmentsYoY, SellThroughYoY, DepletionsYoY, InventoryWeeks, CasesShipped, CasesDepleted, AnomalyType, RiskLevel, Analysis
            FROM Shipments
            WHERE Brand LIKE @brand AND Region LIKE @region
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@brand", $"%{brand.Trim()}%");
        cmd.Parameters.AddWithValue("@region", $"%{region.Trim()}%");

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return new { error = $"No shipment data for brand '{brand}' in region '{region}'.", available_brands = GetAvailableBrands(), available_regions = GetAvailableRegions() };

        var actualBrand = reader.GetString(0);
        var actualRegion = reader.GetString(1);
        var shipYoY = reader.GetString(2);
        var sellYoY = reader.GetString(3);
        var depYoY = reader.GetString(4);
        var invWeeks = reader.GetDouble(5);
        var casesShipped = reader.GetInt32(6);
        var casesDepleted = reader.GetInt32(7);
        var anomalyType = reader.GetString(8);
        var riskLevel = reader.GetString(9);
        var analysis = reader.GetString(10);

        var periodMultiplier = GetPeriodMultiplier(period);

        return new
        {
            brand = actualBrand,
            region = actualRegion,
            period = period ?? "YTD",
            shipments = new
            {
                shipments_yoy = AdjustPercentage(shipYoY, periodMultiplier),
                sell_through_yoy = AdjustPercentage(sellYoY, periodMultiplier),
                depletions_yoy = AdjustPercentage(depYoY, periodMultiplier),
                inventory_weeks_on_hand = Math.Round(invWeeks * (2.0 - periodMultiplier), 1),
                cases_shipped = (int)(casesShipped * periodMultiplier),
                cases_depleted = (int)(casesDepleted * periodMultiplier)
            },
            anomaly = new
            {
                type = anomalyType,
                risk_level = riskLevel,
                is_pipeline_clog = anomalyType == "pipeline_clog",
                shipment_depletion_gap = casesShipped - casesDepleted
            },
            analysis
        };
    }

    public object GetFieldSentiment(string brand, string region)
    {
        if (string.IsNullOrWhiteSpace(brand))
            return new { error = "Parameter 'brand' is required.", available_brands = GetAvailableBrands() };
        if (string.IsNullOrWhiteSpace(region))
            return new { error = "Parameter 'region' is required.", available_regions = GetAvailableRegions() };

        using var conn = OpenConnection();
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Brand, Region, Sentiment
            FROM Sentiment
            WHERE Brand LIKE @brand AND Region LIKE @region
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@brand", $"%{brand.Trim()}%");
        cmd.Parameters.AddWithValue("@region", $"%{region.Trim()}%");

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return new { error = $"No sentiment data for brand '{brand}' in region '{region}'.", available_brands = GetAvailableBrands(), available_regions = GetAvailableRegions() };

        return new
        {
            brand = reader.GetString(0),
            region = reader.GetString(1),
            source = "Field Sales & Distributor Reports",
            reporting_period = "Current YTD",
            sentiment = reader.GetString(2)
        };
    }

    public object GetVariantMix(string brand, string region)
    {
        if (string.IsNullOrWhiteSpace(brand))
            return new { error = "Parameter 'brand' is required.", available_brands = GetAvailableBrands() };

        var normalizedRegion = string.IsNullOrWhiteSpace(region) ? "National" : region.Trim();

        using var conn = OpenConnection();
        conn.Open();

        if (normalizedRegion.Equals("National", StringComparison.OrdinalIgnoreCase) ||
            normalizedRegion.Equals("All", StringComparison.OrdinalIgnoreCase))
        {
            return GetNationalVariantMix(brand.Trim(), conn);
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Brand, Region, Variant, MixPercent, DepletionsYoY
            FROM VariantMix
            WHERE Brand LIKE @brand AND Region LIKE @region
            ORDER BY MixPercent DESC
            """;
        cmd.Parameters.AddWithValue("@brand", $"%{brand.Trim()}%");
        cmd.Parameters.AddWithValue("@region", $"%{normalizedRegion}%");

        var variants = new List<object>();
        string actualBrand = brand, actualRegion = normalizedRegion;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            actualBrand = reader.GetString(0);
            actualRegion = reader.GetString(1);
            variants.Add(new
            {
                variant = reader.GetString(2),
                mix_percent = reader.GetDouble(3),
                depletions_yoy = FormatPercentage(reader.GetDouble(4))
            });
        }

        if (variants.Count == 0)
            return new { error = $"No variant data for brand '{brand}' in region '{normalizedRegion}'.", available_brands = GetAvailableBrands(), available_regions = GetAvailableRegions() };

        return new { brand = actualBrand, region = actualRegion, variants, total_variants = variants.Count };
    }

    private object GetNationalVariantMix(string brand, SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Brand, Variant, AVG(MixPercent) as AvgMix, AVG(DepletionsYoY) as AvgDep
            FROM VariantMix
            WHERE Brand LIKE @brand
            GROUP BY Brand, Variant
            ORDER BY AvgMix DESC
            """;
        cmd.Parameters.AddWithValue("@brand", $"%{brand}%");

        var variants = new List<object>();
        string actualBrand = brand;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            actualBrand = reader.GetString(0);
            variants.Add(new
            {
                variant = reader.GetString(1),
                mix_percent = Math.Round(reader.GetDouble(2), 1),
                depletions_yoy = FormatPercentage(Math.Round(reader.GetDouble(3), 1))
            });
        }

        if (variants.Count == 0)
            return new { error = $"No variant data found for brand '{brand}'.", available_brands = GetAvailableBrands() };

        return new { brand = actualBrand, region = "National", variants, total_variants = variants.Count };
    }

    // ── Update Methods (for AI-driven mutations) ─────────────────────────

    private static readonly Dictionary<string, string[]> ValidTableFields = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Depletions"] = ["DepletionsYoY", "SellThroughYoY", "InventoryWeeks", "Status", "SentimentSummary"],
        ["Shipments"] = ["ShipmentsYoY", "SellThroughYoY", "DepletionsYoY", "InventoryWeeks", "CasesShipped", "CasesDepleted", "AnomalyType", "RiskLevel", "Analysis"],
        ["Sentiment"] = ["Sentiment"]
    };

    private static readonly Dictionary<string, string> CheckStatements = ValidTableFields.Keys
        .ToDictionary(t => t, t => $"SELECT Brand, Region FROM {t} WHERE Brand LIKE @brand AND Region LIKE @region LIMIT 1");

    private static readonly Dictionary<(string Table, string Field), string> UpdateStatements =
        ValidTableFields.SelectMany(kv => kv.Value.Select(f => (Table: kv.Key, Field: f)))
        .ToDictionary(pair => pair, pair => $"UPDATE {pair.Table} SET {pair.Field} = @value WHERE Brand = @brand AND Region = @region");

    public object UpdateMetric(string table, string brand, string region, string field, string value)
    {
        if (!ValidTableFields.TryGetValue(table, out var validFields))
            return new { error = $"Invalid table '{table}'.", valid_tables = ValidTableFields.Keys.ToArray() };

        // Case-insensitive field matching
        var matchedField = validFields.FirstOrDefault(f => f.Equals(field, StringComparison.OrdinalIgnoreCase));
        if (matchedField is null)
            return new { error = $"Invalid field '{field}' for table '{table}'.", valid_fields = validFields };

        // Look up the canonical table name (preserves casing from the dictionary)
        var canonicalTable = ValidTableFields.Keys.First(k => k.Equals(table, StringComparison.OrdinalIgnoreCase));

        if (!CheckStatements.TryGetValue(canonicalTable, out var checkSql))
            return new { error = $"Invalid table '{table}'." };

        if (!UpdateStatements.TryGetValue((canonicalTable, matchedField), out var updateSql))
            return new { error = $"Invalid field '{field}' for table '{table}'." };

        using var conn = OpenConnection();
        conn.Open();

        // First verify the record exists
        using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = checkSql;
        checkCmd.Parameters.AddWithValue("@brand", $"%{brand.Trim()}%");
        checkCmd.Parameters.AddWithValue("@region", $"%{region.Trim()}%");

        string actualBrand, actualRegion;
        using (var reader = checkCmd.ExecuteReader())
        {
            if (!reader.Read())
                return new { error = $"No record found for brand '{brand}' in region '{region}' in {table}.", available_brands = GetAvailableBrands(), available_regions = GetAvailableRegions() };
            actualBrand = reader.GetString(0);
            actualRegion = reader.GetString(1);
        }

        // Execute the update using the pre-built SQL and exact brand/region from DB
        using var updateCmd = conn.CreateCommand();
        updateCmd.CommandText = updateSql;
        updateCmd.Parameters.AddWithValue("@value", value);
        updateCmd.Parameters.AddWithValue("@brand", actualBrand);
        updateCmd.Parameters.AddWithValue("@region", actualRegion);
        var rows = updateCmd.ExecuteNonQuery();

        return new
        {
            success = true,
            table,
            brand = actualBrand,
            region = actualRegion,
            field = matchedField,
            new_value = value,
            rows_affected = rows
        };
    }

    // ── National Aggregation ─────────────────────────────────────────────

    private object GetNationalDepletionStats(string brand, string? period)
    {
        using var conn = OpenConnection();
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Brand, Region, DepletionsYoY, SellThroughYoY, InventoryWeeks, Status
            FROM Depletions
            WHERE Brand LIKE @brand
            """;
        cmd.Parameters.AddWithValue("@brand", $"%{NormalizeDiacritics(brand)}%");

        var rows = new List<(string Brand, string Region, string DepYoY, string SellYoY, double InvWeeks, string Status)>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
                rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetDouble(4), reader.GetString(5)));
        }

        if (rows.Count == 0)
            return new { error = $"No data found for brand '{brand}'.", available_brands = GetAvailableBrands() };

        var periodMultiplier = GetPeriodMultiplier(period);

        var avgDepletions = rows.Average(r => ParsePercentage(r.DepYoY));
        var avgSellThrough = rows.Average(r => ParsePercentage(r.SellYoY));
        var avgInventory = rows.Average(r => r.InvWeeks);

        return new
        {
            brand = rows[0].Brand,
            region = "National",
            period = period ?? "YTD",
            regions_aggregated = rows.Count,
            metrics = new
            {
                depletions_yoy = FormatPercentage(Math.Round(avgDepletions * periodMultiplier, 1)),
                sell_through_yoy = FormatPercentage(Math.Round(avgSellThrough * periodMultiplier, 1)),
                inventory_weeks_on_hand = Math.Round(avgInventory * (2.0 - periodMultiplier), 1),
                status = avgDepletions > 0 ? "Growing" : avgDepletions > -2 ? "Stable" : "Declining"
            },
            regional_breakdown = rows.Select(r => new
            {
                region = r.Region,
                depletions_yoy = AdjustPercentage(r.DepYoY, periodMultiplier),
                status = r.Status
            }).ToArray()
        };
    }

    // ── Connection Helper ────────────────────────────────────────────────

    private SqliteConnection OpenConnection() => new(_connectionString);

    // ── Lookup Helpers ───────────────────────────────────────────────────

    private string[] GetAvailableBrands() =>
        _tenant.Brands.Select(b => b.Name).ToArray();

    private string[] GetAvailableRegions() =>
        [.. _tenant.Regions, "National"];

    // ── Data Generation Helpers (same algorithms as SimulatedMetricsData) ─

    private static double GetBaseTrend(BrandConfig brand, int seed)
    {
        var rng = new Random(seed);
        var categoryBoost = brand.Category switch
        {
            "Tequila" => 4.0 + rng.NextDouble() * 4.0,
            "Mezcal" => 5.0 + rng.NextDouble() * 5.0,
            "Bourbon" => 3.0 + rng.NextDouble() * 5.0,
            "Ready-to-Drink" => 2.0 + rng.NextDouble() * 6.0,
            "Gin" => 1.0 + rng.NextDouble() * 3.0,
            "Rum" => -2.0 + rng.NextDouble() * 4.0,
            "Vodka" => -3.0 + rng.NextDouble() * 3.0,
            _ => -1.0 + rng.NextDouble() * 4.0
        };
        var segmentBoost = brand.PriceSegment switch
        {
            "Ultra-Premium" => 2.0,
            "Premium" => 0.5,
            _ => -1.0
        };
        return Math.Round(categoryBoost + segmentBoost, 1);
    }

    private static string DetermineDepletionStatus(double depletionGrowth, double sellThroughGrowth, double inventoryWeeks)
    {
        if (inventoryWeeks > 8.5 && sellThroughGrowth < 0) return "Overstocked";
        if (depletionGrowth > 6.0 && sellThroughGrowth > 4.0) return "Growth Leader";
        if (depletionGrowth < -1.0 || sellThroughGrowth < -2.0) return "Declining";
        return "On Track";
    }

    private static (string AnomalyType, string RiskLevel) DetermineAnomalyType(
        double shipmentGrowth, double sellThroughGrowth, double depletionGrowth, double inventoryWeeks)
    {
        var gap = shipmentGrowth - sellThroughGrowth;

        if (gap > 6.0 && inventoryWeeks > 8.0)
            return ("pipeline_clog", inventoryWeeks > 10.0 ? "critical" : "high");
        if (shipmentGrowth < -4.0 && depletionGrowth > 4.0)
            return ("supply_constraint", depletionGrowth > 8.0 ? "critical" : "high");
        if (shipmentGrowth > 8.0 && sellThroughGrowth > 5.0 && inventoryWeeks < 5.0)
            return ("growth_opportunity", "low");
        if (gap > 3.0 && inventoryWeeks > 5.0)
            return ("pipeline_building", "medium");
        if (depletionGrowth < 0 && shipmentGrowth < 0 && Math.Abs(gap) < 2.0)
            return ("declining_aligned", inventoryWeeks > 7.0 ? "medium" : "low");
        return ("healthy", "low");
    }

    // ── Narrative Generation ─────────────────────────────────────────────

    private string GenerateDepletionSummary(BrandConfig brand, string region,
        double depletionGrowth, double sellThroughGrowth, double inventoryWeeks, string status, Random rng)
    {
        var variants = brand.Variants.Count > 0 ? brand.Variants[rng.Next(brand.Variants.Count)] : "core";

        return status switch
        {
            "Growth Leader" => $"{brand.Name} showing strong momentum in {region} with {FormatPercentage(depletionGrowth)} depletion growth. " +
                $"The {variants} variant is driving trial in on-premise accounts. Distributor velocity up across key retail chains. " +
                $"Weeks on hand at {inventoryWeeks} indicates healthy pipeline with strong consumer pull.",

            "Overstocked" => $"Distributor warehouses in {region} holding excess {brand.Name} inventory at {inventoryWeeks} weeks on hand. " +
                $"Sell-through at {FormatPercentage(sellThroughGrowth)} suggests consumer demand softening. " +
                $"Competitive pressure in the {brand.Category.ToLower()} segment is creating headwinds. Promotional support may be needed to clear pipeline.",

            "Declining" => $"{brand.Name} facing headwinds in {region} with depletions at {FormatPercentage(depletionGrowth)}. " +
                $"Category dynamics shifting as consumers explore alternatives in the {brand.PriceSegment.ToLower()} {brand.Category.ToLower()} tier. " +
                $"On-premise placements under pressure. Field team recommends targeted activation programs.",

            _ => $"{brand.Name} performing steadily in {region} with depletions at {FormatPercentage(depletionGrowth)}. " +
                $"The {variants} expression maintaining velocity in both on- and off-premise channels. " +
                $"Inventory at {inventoryWeeks} weeks — well within target range. Distribution holding stable."
        };
    }

    private string GenerateShipmentAnalysis(BrandConfig brand, string region,
        double shipmentGrowth, double sellThroughGrowth, double depletionGrowth,
        double inventoryWeeks, int casesShipped, int casesDepleted, string anomalyType)
    {
        var gap = casesShipped - casesDepleted;
        var model = _tenant.Distribution?.Model ?? "Three-Tier";

        return anomalyType switch
        {
            "pipeline_clog" => $"Shipments to distributors in {region} are up {FormatPercentage(shipmentGrowth)}, but consumer sell-through is at {FormatPercentage(sellThroughGrowth)}. " +
                $"Distributor warehouses holding {casesShipped:N0} cases shipped vs {casesDepleted:N0} depleted — a {gap:N0} case gap. " +
                $"At {inventoryWeeks} weeks on hand, the {model} pipeline is building pressure. " +
                $"Recommend reducing shipments and deploying depletion programs to clear excess inventory.",

            "supply_constraint" => $"Cannot ship enough {brand.Name} to meet {region} demand. Shipments at {FormatPercentage(shipmentGrowth)} while " +
                $"consumer demand grows {FormatPercentage(depletionGrowth)}. Key accounts requesting more allocation. " +
                $"Lost some placements to competitors who can supply. Strategic allocation decisions needed to protect highest-value accounts.",

            "growth_opportunity" => $"Shipping aggressively to fill new {brand.Name} distribution in {region} — but sell-through is keeping pace. " +
                $"Distributors requesting more allocation. Retail chains expanding shelf space. " +
                $"Healthy growth pipeline at {inventoryWeeks} weeks on hand. Every new distribution point converting at above-average velocity.",

            "pipeline_building" => $"Shipping ahead of sell-through as {brand.Name} distribution expands in {region}. " +
                $"{casesShipped:N0} cases shipped vs {casesDepleted:N0} depleted. Expected pipeline fill during distribution expansion — " +
                $"monitoring to ensure new points of distribution convert. Weeks on hand at {inventoryWeeks} within acceptable range.",

            "declining_aligned" => $"Shipments and sell-through for {brand.Name} both declining in {region} at similar rates — pipeline is stable but the floor is dropping. " +
                $"The {brand.Category.ToLower()} segment faces structural headwinds. Volume management is appropriate but category strategy review needed.",

            _ => $"Shipments and sell-through aligned for {brand.Name} in {region}. {casesShipped:N0} cases shipped, {casesDepleted:N0} depleted. " +
                $"The {model} distribution pipeline is balanced at {inventoryWeeks} weeks on hand. No anomalies detected — healthy flow through the system."
        };
    }

    private string GenerateFieldSentiment(BrandConfig brand, string region, double baseTrend, Random rng)
    {
        var channel = _tenant.Channels[rng.Next(_tenant.Channels.Count)];
        var sentences = new List<string>();

        if (baseTrend > 3.0)
        {
            sentences.Add($"Strong positive sentiment for {brand.Name} across {region} distributors.");
            sentences.Add($"{channel} accounts reporting increased pull. Consumer interest in the {brand.PriceSegment.ToLower()} {brand.Category.ToLower()} segment remains robust.");
            sentences.Add($"Distributor reps requesting expanded allocation. Retail partners signaling increased shelf space commitment.");
            sentences.Add($"Field team confidence high — brand momentum creating opportunities for menu placement and promotional partnerships.");
        }
        else if (baseTrend > 0)
        {
            sentences.Add($"Generally positive outlook for {brand.Name} in {region}. Steady demand across {channel} accounts.");
            sentences.Add($"The {brand.Category.ToLower()} category maintaining share in a competitive environment.");
            sentences.Add($"Distributors note the brand benefits from established consumer loyalty and reliable supply chain execution.");
        }
        else if (baseTrend > -1.0)
        {
            sentences.Add($"{brand.Name} holding flat in {region} with mixed signals from the field.");
            sentences.Add($"Some {channel} accounts reporting steady velocity while others see softening. The {brand.Category.ToLower()} category overall is under moderate pressure.");
            sentences.Add($"Price sensitivity increasing among consumers at the {brand.PriceSegment.ToLower()} tier.");
            sentences.Add($"Field team recommends targeted promotional support and menu placement programs to defend share.");
        }
        else
        {
            sentences.Add($"{brand.Name} facing headwinds in {region}. Distributor sentiment cautious.");
            sentences.Add($"The {brand.PriceSegment.ToLower()} {brand.Category.ToLower()} segment is losing occasions to other spirit categories. {channel} placements under pressure.");
            sentences.Add($"Competitors gaining shelf space. Consumers migrating to trending categories.");
            sentences.Add($"Need strategic review of the brand's position in {region}. Promotional spend showing diminishing returns — may need positioning refresh.");
        }

        return string.Join(" ", sentences);
    }

    // ── Utility Methods ──────────────────────────────────────────────────

    private static double GetPeriodMultiplier(string? period) =>
        period?.Trim().ToUpperInvariant() switch
        {
            "Q1" => 0.85, "Q2" => 1.05, "Q3" => 0.95, "Q4" => 1.15, _ => 1.0
        };

    private static double ParsePercentage(string pct) =>
        double.TryParse(pct.TrimEnd('%').TrimStart('+'), out var val) ? val : 0;

    private static string FormatPercentage(double value) =>
        (value >= 0 ? "+" : "") + value.ToString("F1") + "%";

    private static string AdjustPercentage(string pct, double multiplier)
    {
        if (double.TryParse(pct.TrimEnd('%').TrimStart('+'), out var val))
        {
            var adjusted = Math.Round(val * multiplier, 1);
            return (adjusted >= 0 ? "+" : "") + adjusted + "%";
        }
        return pct;
    }

    private static int GetStableHash(string input)
    {
        unchecked
        {
            int hash = 17;
            foreach (var c in input)
                hash = hash * 31 + c;
            return Math.Abs(hash);
        }
    }

    private static string NormalizeDiacritics(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}
