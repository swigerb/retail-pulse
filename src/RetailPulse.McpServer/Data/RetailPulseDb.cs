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

            CREATE TABLE IF NOT EXISTS DemandHistory (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Brand TEXT NOT NULL COLLATE NOCASE,
                Region TEXT NOT NULL COLLATE NOCASE,
                Channel TEXT NOT NULL COLLATE NOCASE,
                Date TEXT NOT NULL,
                Volume REAL NOT NULL,
                Units INTEGER NOT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_DemandHistory_BrandRegion ON DemandHistory (Brand, Region);
            CREATE INDEX IF NOT EXISTS IX_DemandHistory_Date ON DemandHistory (Date);

            CREATE TABLE IF NOT EXISTS SeasonalFactors (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Category TEXT NOT NULL COLLATE NOCASE,
                Month INTEGER NOT NULL,
                Multiplier REAL NOT NULL,
                EventName TEXT,
                Description TEXT
            );

            CREATE INDEX IF NOT EXISTS IX_SeasonalFactors_Category ON SeasonalFactors (Category);

            CREATE TABLE IF NOT EXISTS DemandHistory (
                Brand TEXT NOT NULL COLLATE NOCASE,
                Region TEXT NOT NULL COLLATE NOCASE,
                Channel TEXT NOT NULL COLLATE NOCASE,
                Date TEXT NOT NULL,
                Volume REAL NOT NULL,
                Units INTEGER NOT NULL,
                PRIMARY KEY (Brand, Region, Channel, Date)
            );

            CREATE TABLE IF NOT EXISTS SeasonalFactors (
                Category TEXT NOT NULL COLLATE NOCASE,
                Month INTEGER NOT NULL,
                Multiplier REAL NOT NULL,
                EventName TEXT,
                Description TEXT,
                PRIMARY KEY (Category, Month)
            );

            CREATE TABLE IF NOT EXISTS PromoHistory (
                Brand TEXT NOT NULL,
                Region TEXT NOT NULL,
                PromoType TEXT NOT NULL,
                CampaignName TEXT NOT NULL,
                StartDate TEXT NOT NULL,
                EndDate TEXT NOT NULL,
                Spend REAL NOT NULL,
                BaselineVolume REAL NOT NULL,
                ActualVolume REAL NOT NULL,
                LiftPercent REAL NOT NULL,
                ROI REAL NOT NULL,
                SuccessRating TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS LiftCoefficients (
                Category TEXT NOT NULL,
                PromoType TEXT NOT NULL,
                AvgLiftPercent REAL NOT NULL,
                StdDev REAL NOT NULL,
                MinSpend REAL NOT NULL,
                MaxEffectiveSpend REAL NOT NULL,
                PRIMARY KEY (Category, PromoType)
            );

            CREATE TABLE IF NOT EXISTS CompetitorPricing (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Competitor TEXT NOT NULL COLLATE NOCASE,
                Brand TEXT NOT NULL COLLATE NOCASE,
                Category TEXT NOT NULL COLLATE NOCASE,
                Region TEXT NOT NULL COLLATE NOCASE,
                Price REAL NOT NULL,
                PreviousPrice REAL,
                PriceChangePercent REAL,
                EffectiveDate TEXT NOT NULL,
                Source TEXT
            );

            CREATE INDEX IF NOT EXISTS IX_CompetitorPricing_BrandRegion ON CompetitorPricing (Brand, Region);
            CREATE INDEX IF NOT EXISTS IX_CompetitorPricing_Competitor ON CompetitorPricing (Competitor);

            CREATE TABLE IF NOT EXISTS MarketShare (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Brand TEXT NOT NULL COLLATE NOCASE,
                Category TEXT NOT NULL COLLATE NOCASE,
                Region TEXT NOT NULL COLLATE NOCASE,
                Period TEXT NOT NULL COLLATE NOCASE,
                SharePercent REAL NOT NULL,
                PreviousSharePercent REAL,
                ShareChangePoints REAL,
                Source TEXT
            );

            CREATE INDEX IF NOT EXISTS IX_MarketShare_BrandRegion ON MarketShare (Brand, Region);

            CREATE TABLE IF NOT EXISTS CompetitorActivity (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Competitor TEXT NOT NULL COLLATE NOCASE,
                ActivityType TEXT NOT NULL COLLATE NOCASE,
                Category TEXT NOT NULL COLLATE NOCASE,
                Region TEXT NOT NULL COLLATE NOCASE,
                Description TEXT,
                Impact TEXT,
                DetectedDate TEXT NOT NULL,
                ResponseRecommendation TEXT
            );

            CREATE INDEX IF NOT EXISTS IX_CompetitorActivity_Category ON CompetitorActivity (Category, Region);

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
        clearCmd.CommandText = "DELETE FROM Depletions; DELETE FROM Shipments; DELETE FROM Sentiment; DELETE FROM VariantMix; DELETE FROM DemandHistory; DELETE FROM SeasonalFactors; DELETE FROM PromoHistory; DELETE FROM LiftCoefficients; DELETE FROM CompetitorPricing; DELETE FROM MarketShare; DELETE FROM CompetitorActivity; DELETE FROM SeedMetadata;";
        clearCmd.ExecuteNonQuery();

        SeedDepletions(conn);
        SeedShipments(conn);
        SeedSentiment(conn);
        SeedVariantMix(conn);
        SeedDemandHistory(conn);
        SeedSeasonalFactors(conn);
        SeedPromoHistory(conn);
        SeedLiftCoefficients(conn);
        SeedCompetitorPricing(conn);
        SeedMarketShare(conn);
        SeedCompetitorActivity(conn);

        // Store hash
        using var hashCmd = conn.CreateCommand();
        hashCmd.CommandText = "INSERT INTO SeedMetadata (Key, Value) VALUES ('tenant_hash', @hash)";
        hashCmd.Parameters.AddWithValue("@hash", currentHash);
        hashCmd.ExecuteNonQuery();

        tx.Commit();
    }

    // Bump this version whenever the schema or seeding logic changes
    // to force a re-seed even if tenant.yaml hasn't changed.
    private const int SchemaVersion = 5;

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

    // ── Demand History & Seasonal Factors Seeding ──────────────────────────

    private void SeedDemandHistory(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO DemandHistory (Brand, Region, Channel, Date, Volume, Units)
            VALUES (@brand, @region, @channel, @date, @volume, @units)
            """;

        var pBrand = cmd.Parameters.Add("@brand", SqliteType.Text);
        var pRegion = cmd.Parameters.Add("@region", SqliteType.Text);
        var pChannel = cmd.Parameters.Add("@channel", SqliteType.Text);
        var pDate = cmd.Parameters.Add("@date", SqliteType.Text);
        var pVolume = cmd.Parameters.Add("@volume", SqliteType.Real);
        var pUnits = cmd.Parameters.Add("@units", SqliteType.Integer);

        // Generate 365 days ending "today" (May 13, 2026)
        var endDate = new DateOnly(2026, 5, 13);
        var startDate = endDate.AddDays(-364);

        foreach (var brand in _tenant.Brands)
        {
            var baseVolume = GetBaseVolume(brand);
            var brandSeed = GetStableHash($"demand|{brand.Name}");

            foreach (var region in _tenant.Regions)
            {
                var regionSeed = GetStableHash($"demand|{brand.Name}|{region}");

                // Inject 1-2 anomalies per brand (shared across regions for visibility)
                var anomalyRng = new Random(brandSeed + 7);
                var anomalyDay1 = anomalyRng.Next(60, 300);
                var anomalyDay2 = anomalyRng.Next(60, 300);
                while (Math.Abs(anomalyDay2 - anomalyDay1) < 30)
                    anomalyDay2 = anomalyRng.Next(60, 300);
                var anomalyType1 = anomalyRng.NextDouble() > 0.5; // true = spike, false = drop

                foreach (var channel in _tenant.Channels)
                {
                    var channelSeed = GetStableHash($"demand|{brand.Name}|{region}|{channel}");
                    var rng = new Random(channelSeed);

                    var channelShare = channel switch
                    {
                        "Off-Premise" => 0.50,
                        "On-Premise" => 0.30,
                        "E-Commerce" => 0.20,
                        _ => 0.33
                    };

                    var channelBase = baseVolume * channelShare;
                    // Regional variance ±15%
                    var regionFactor = 0.85 + (new Random(regionSeed).NextDouble() * 0.30);
                    channelBase *= regionFactor;

                    // Linear trend slope (±0.05% per day)
                    var trendSlope = (rng.NextDouble() - 0.45) * 0.001;

                    for (int dayOffset = 0; dayOffset < 365; dayOffset++)
                    {
                        var date = startDate.AddDays(dayOffset);
                        var month = date.Month;

                        // Seasonal multiplier based on category
                        var seasonal = GetCategorySeasonalMultiplier(brand.Category, month);

                        // Day-of-week pattern (weekends higher for QSR/Spirits)
                        var dow = date.DayOfWeek;
                        var dowFactor = (brand.Category is "Quick-Serve Restaurant" or "Spirits") && (dow is DayOfWeek.Friday or DayOfWeek.Saturday)
                            ? 1.15 : 1.0;

                        // Trend component
                        var trendFactor = 1.0 + trendSlope * dayOffset;

                        // Random noise ±8%
                        var noise = 0.92 + rng.NextDouble() * 0.16;

                        var volume = channelBase * seasonal * dowFactor * trendFactor * noise;

                        // Apply anomalies
                        if (Math.Abs(dayOffset - anomalyDay1) <= 3)
                            volume *= anomalyType1 ? 1.45 : 0.55;
                        if (Math.Abs(dayOffset - anomalyDay2) <= 2)
                            volume *= anomalyType1 ? 0.60 : 1.40;

                        volume = Math.Max(1.0, volume);
                        var units = Math.Max(1, (int)(volume / 28.0)); // ~28 volume per unit (case equivalent)

                        pBrand.Value = brand.Name;
                        pRegion.Value = region;
                        pChannel.Value = channel;
                        pDate.Value = date.ToString("yyyy-MM-dd");
                        pVolume.Value = Math.Round(volume, 2);
                        pUnits.Value = units;
                        cmd.ExecuteNonQuery();
                    }
                }
            }
        }
    }

    private static double GetBaseVolume(BrandConfig brand)
    {
        // Bigger brands = more daily volume
        return brand.Category switch
        {
            "Grocery" => 1200.0,
            "Quick-Serve Restaurant" => 900.0,
            "Spirits" => 350.0,
            "Home Improvement" => 600.0,
            "Office Supply" => 400.0,
            "Furniture" => 250.0,
            _ => 500.0
        } * (brand.PriceSegment == "Premium" ? 0.8 : 1.0);
    }

    private static double GetCategorySeasonalMultiplier(string category, int month)
    {
        return category switch
        {
            "Spirits" => month switch
            {
                11 => 1.30, 12 => 1.40,       // Holidays
                6 => 1.10, 7 => 1.15,          // Summer entertaining
                1 => 0.85, 2 => 0.90,          // Post-holiday dip
                _ => 1.0
            },
            "Grocery" => month switch
            {
                8 => 1.20, 9 => 1.25,          // Back-to-school
                11 => 1.25, 12 => 1.30,        // Holidays
                1 => 0.90, 2 => 0.92,          // Post-holiday
                _ => 1.0
            },
            "Quick-Serve Restaurant" => month switch
            {
                6 => 1.15, 7 => 1.20, 8 => 1.18, // Summer
                1 => 0.88, 2 => 0.90,             // Winter dip
                12 => 0.95,                        // Holiday competition
                _ => 1.0
            },
            "Home Improvement" => month switch
            {
                3 => 1.20, 4 => 1.30, 5 => 1.35, // Spring projects
                9 => 1.20,                          // Fall prep
                1 => 0.80, 2 => 0.82,              // Winter low
                12 => 0.85,                         // Winter
                _ => 1.0
            },
            "Office Supply" => month switch
            {
                8 => 1.25, 9 => 1.20,          // Back-to-school
                1 => 1.15,                       // New year office setup
                6 => 0.90, 7 => 0.88,           // Summer lull
                _ => 1.0
            },
            "Furniture" => month switch
            {
                3 => 1.15, 4 => 1.10,          // Spring refresh
                8 => 1.20, 9 => 1.15,          // Back-to-school / dorm
                11 => 1.25, 12 => 1.10,        // Holiday gifting
                1 => 0.80, 2 => 0.85,          // Post-holiday
                _ => 1.0
            },
            _ => 1.0
        };
    }

    private void SeedSeasonalFactors(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO SeasonalFactors (Category, Month, Multiplier, EventName, Description)
            VALUES (@category, @month, @multiplier, @event, @desc)
            """;

        var pCat = cmd.Parameters.Add("@category", SqliteType.Text);
        var pMonth = cmd.Parameters.Add("@month", SqliteType.Integer);
        var pMult = cmd.Parameters.Add("@multiplier", SqliteType.Real);
        var pEvent = cmd.Parameters.Add("@event", SqliteType.Text);
        var pDesc = cmd.Parameters.Add("@desc", SqliteType.Text);

        var factors = new (string Category, int Month, double Mult, string? Event, string Desc)[]
        {
            // Spirits
            ("Spirits", 1, 0.85, "Post-Holiday Dip", "Consumer spending contracts after holiday season; spirits purchases decline sharply in January"),
            ("Spirits", 2, 0.90, "Post-Holiday Recovery", "Gradual recovery from January dip; Valentine's Day provides modest lift"),
            ("Spirits", 6, 1.10, "Summer Entertaining", "Outdoor entertaining season begins; cocktail culture drives spirits demand"),
            ("Spirits", 7, 1.15, "Peak Summer", "July 4th celebrations and peak summer entertaining drive spirits sales"),
            ("Spirits", 11, 1.30, "Thanksgiving", "Thanksgiving gatherings and early holiday gifting boost spirits significantly"),
            ("Spirits", 12, 1.40, "Holiday Season", "Christmas, New Year's Eve, and holiday gifting create peak spirits demand"),

            // Grocery
            ("Grocery", 1, 0.90, "Post-Holiday Reset", "Consumers shift to healthier eating; reduced spending after holidays"),
            ("Grocery", 2, 0.92, "Winter Lull", "Continued lower traffic; Super Bowl provides one-week spike only"),
            ("Grocery", 8, 1.20, "Back-to-School", "Families stock up on school lunches, snacks, and meal prep essentials"),
            ("Grocery", 9, 1.25, "Peak Back-to-School", "Full school routines drive consistent grocery spending; fall meal planning"),
            ("Grocery", 11, 1.25, "Thanksgiving Prep", "Largest cooking holiday drives massive grocery volume across all categories"),
            ("Grocery", 12, 1.30, "Holiday Entertaining", "Holiday parties, baking, and family gatherings sustain elevated demand"),

            // Quick-Serve Restaurant
            ("Quick-Serve Restaurant", 1, 0.88, "Winter Dip", "Cold weather and post-holiday budget tightening reduce QSR traffic"),
            ("Quick-Serve Restaurant", 2, 0.90, "Continued Winter", "Lingering winter weather suppresses foot traffic; budget recovery continues"),
            ("Quick-Serve Restaurant", 6, 1.15, "Summer Start", "School's out, families eat out more; summer travel begins"),
            ("Quick-Serve Restaurant", 7, 1.20, "Peak Summer", "Peak travel and outdoor activity season; highest QSR traffic"),
            ("Quick-Serve Restaurant", 8, 1.18, "Late Summer", "Continued summer momentum; back-to-school transitions begin"),
            ("Quick-Serve Restaurant", 12, 0.95, "Holiday Competition", "Holiday home cooking and sit-down restaurants compete for dining occasions"),

            // Home Improvement
            ("Home Improvement", 1, 0.80, "Winter Low", "Shortest days, coldest weather; outdoor projects impossible in most regions"),
            ("Home Improvement", 2, 0.82, "Late Winter", "Planning begins but execution still limited by weather"),
            ("Home Improvement", 3, 1.20, "Spring Awakening", "Spring project planning converts to purchases; garden prep begins"),
            ("Home Improvement", 4, 1.30, "Peak Spring", "Prime project season — landscaping, painting, deck building in full swing"),
            ("Home Improvement", 5, 1.35, "Spring Peak", "Highest demand period; Memorial Day weekend sales event drives volume"),
            ("Home Improvement", 9, 1.20, "Fall Prep", "Winterization projects, fall landscaping, and pre-holiday home improvements"),
            ("Home Improvement", 12, 0.85, "Winter Decline", "Outdoor projects cease; holiday focus shifts spending to gifts"),

            // Office Supply
            ("Office Supply", 1, 1.15, "New Year Setup", "New year office reorganization and budget spending drives demand"),
            ("Office Supply", 6, 0.90, "Summer Lull", "School's out, offices at reduced capacity; lowest demand period"),
            ("Office Supply", 7, 0.88, "Deep Summer", "Continued summer slowdown; vacation season reduces office supply needs"),
            ("Office Supply", 8, 1.25, "Back-to-School Peak", "Massive back-to-school demand for supplies, technology, and furniture"),
            ("Office Supply", 9, 1.20, "School Continuation", "Ongoing school-year supply needs; corporate Q3 budget refresh"),

            // Furniture
            ("Furniture", 1, 0.80, "Post-Holiday Low", "Consumer spending exhausted from holidays; lowest furniture demand"),
            ("Furniture", 2, 0.85, "Winter Slow", "Presidents' Day sales provide brief lift but overall demand remains low"),
            ("Furniture", 3, 1.15, "Spring Refresh", "Spring cleaning and home refresh drive new furniture purchases"),
            ("Furniture", 4, 1.10, "Spring Continuation", "Moving season begins; apartment leases turn over"),
            ("Furniture", 8, 1.20, "Back-to-School / Dorm", "College dorm furnishing and apartment setup for new school year"),
            ("Furniture", 9, 1.15, "Fall Nesting", "Fall nesting instinct; consumers invest in home comfort as weather cools"),
            ("Furniture", 11, 1.25, "Holiday Gifting", "Black Friday and holiday furniture sales; largest promotional period"),
            ("Furniture", 12, 1.10, "Holiday Sales", "Continued holiday sales momentum; gift card redemption begins"),
        };

        foreach (var (cat, month, mult, evt, desc) in factors)
        {
            pCat.Value = cat;
            pMonth.Value = month;
            pMult.Value = mult;
            pEvent.Value = evt ?? (object)DBNull.Value;
            pDesc.Value = desc;
            cmd.ExecuteNonQuery();
        }
    }

    // ── Demand Query Methods ─────────────────────────────────────────────

    public object GetHistoricalDemand(string? brand, string? region = null, string? channel = null, int months = 12)
    {
        months = Math.Clamp(months, 1, 24);
        var endDate = new DateOnly(2026, 5, 13);
        var startDate = endDate.AddMonths(-months);

        using var conn = OpenConnection();
        conn.Open();

        var sql = new StringBuilder("""
            SELECT Brand, Region, Channel, Date, Volume, Units
            FROM DemandHistory
            WHERE Date >= @start AND Date <= @end
            """);
        using var cmd = conn.CreateCommand();
        cmd.Parameters.AddWithValue("@start", startDate.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("@end", endDate.ToString("yyyy-MM-dd"));

        if (!string.IsNullOrWhiteSpace(brand))
        {
            sql.Append(" AND Brand LIKE @brand");
            cmd.Parameters.AddWithValue("@brand", $"%{brand.Trim()}%");
        }
        if (!string.IsNullOrWhiteSpace(region))
        {
            sql.Append(" AND Region LIKE @region");
            cmd.Parameters.AddWithValue("@region", $"%{region.Trim()}%");
        }
        if (!string.IsNullOrWhiteSpace(channel))
        {
            sql.Append(" AND Channel LIKE @channel");
            cmd.Parameters.AddWithValue("@channel", $"%{channel.Trim()}%");
        }

        // Aggregate to weekly for readability
        sql.Append("""
            
            ORDER BY Brand, Region, Date
            """);
        cmd.CommandText = sql.ToString();

        var rows = new List<object>();
        var totalVolume = 0.0;
        var totalUnits = 0;
        string? firstBrand = null;

        using var reader = cmd.ExecuteReader();
        // Aggregate into weekly buckets for manageable output
        var weekBucket = new Dictionary<string, (string Brand, string Region, string Channel, string WeekStart, double Volume, int Units, int Days)>();

        while (reader.Read())
        {
            var b = reader.GetString(0);
            var r = reader.GetString(1);
            var ch = reader.GetString(2);
            var d = reader.GetString(3);
            var v = reader.GetDouble(4);
            var u = reader.GetInt32(5);

            firstBrand ??= b;
            totalVolume += v;
            totalUnits += u;

            var dateObj = DateOnly.Parse(d);
            // ISO week start (Monday)
            var daysSinceMonday = ((int)dateObj.DayOfWeek + 6) % 7;
            var weekStart = dateObj.AddDays(-daysSinceMonday).ToString("yyyy-MM-dd");
            var key = $"{b}|{r}|{ch}|{weekStart}";

            if (weekBucket.TryGetValue(key, out var existing))
                weekBucket[key] = (b, r, ch, weekStart, existing.Volume + v, existing.Units + u, existing.Days + 1);
            else
                weekBucket[key] = (b, r, ch, weekStart, v, u, 1);
        }

        var weeklyData = weekBucket.Values
            .OrderBy(w => w.Brand).ThenBy(w => w.Region).ThenBy(w => w.WeekStart)
            .Select(w => new
            {
                brand = w.Brand,
                region = w.Region,
                channel = w.Channel,
                week_starting = w.WeekStart,
                volume = Math.Round(w.Volume, 1),
                units = w.Units,
                avg_daily_volume = Math.Round(w.Volume / w.Days, 1)
            })
            .ToList();

        return new
        {
            period = new { start = startDate.ToString("yyyy-MM-dd"), end = endDate.ToString("yyyy-MM-dd"), months },
            filters = new { brand = brand ?? "all", region = region ?? "all", channel = channel ?? "all" },
            summary = new
            {
                total_volume = Math.Round(totalVolume, 1),
                total_units = totalUnits,
                weeks_of_data = weeklyData.Count,
                avg_weekly_volume = weeklyData.Count > 0 ? Math.Round(totalVolume / weeklyData.Select(w => w.week_starting).Distinct().Count(), 1) : 0
            },
            weekly_data = weeklyData
        };
    }

    public object GenerateForecast(string brand, string? region = null, int days = 90)
    {
        if (string.IsNullOrWhiteSpace(brand))
            return new { error = "Parameter 'brand' is required.", available_brands = GetAvailableBrands() };

        days = Math.Clamp(days, 7, 365);
        var forecastStart = new DateOnly(2026, 5, 14); // Day after "today"
        var historyStart = new DateOnly(2025, 5, 14); // 12 months of history

        using var conn = OpenConnection();
        conn.Open();

        // Get trailing 90 days of actual data for trend calculation
        var trailing90Start = new DateOnly(2026, 2, 12);
        using var histCmd = conn.CreateCommand();
        histCmd.CommandText = """
            SELECT Date, SUM(Volume) as DayVolume, SUM(Units) as DayUnits
            FROM DemandHistory
            WHERE Brand LIKE @brand AND Date >= @start AND Date <= @end
            """;
        if (!string.IsNullOrWhiteSpace(region))
        {
            histCmd.CommandText += " AND Region LIKE @region";
            histCmd.Parameters.AddWithValue("@region", $"%{region.Trim()}%");
        }
        histCmd.CommandText += " GROUP BY Date ORDER BY Date";
        histCmd.Parameters.AddWithValue("@brand", $"%{brand.Trim()}%");
        histCmd.Parameters.AddWithValue("@start", trailing90Start.ToString("yyyy-MM-dd"));
        histCmd.Parameters.AddWithValue("@end", "2026-05-13");

        var historicalDays = new List<(DateOnly Date, double Volume)>();
        using (var reader = histCmd.ExecuteReader())
        {
            while (reader.Read())
                historicalDays.Add((DateOnly.Parse(reader.GetString(0)), reader.GetDouble(1)));
        }

        if (historicalDays.Count < 7)
            return new { error = $"Insufficient historical data for brand '{brand}'. Need at least 7 days.", available_brands = GetAvailableBrands() };

        // Trailing 30-day average
        var trailing30 = historicalDays.TakeLast(30).Average(d => d.Volume);

        // Linear regression on 90 days for trend slope
        var n = historicalDays.Count;
        var xMean = (n - 1) / 2.0;
        var yMean = historicalDays.Average(d => d.Volume);
        var numerator = 0.0;
        var denominator = 0.0;
        for (int i = 0; i < n; i++)
        {
            numerator += (i - xMean) * (historicalDays[i].Volume - yMean);
            denominator += (i - xMean) * (i - xMean);
        }
        var trendSlope = denominator > 0 ? numerator / denominator / yMean : 0; // Normalized daily slope

        // Determine category for seasonal factors
        var matchingBrand = _tenant.Brands.FirstOrDefault(b =>
            b.Name.Contains(brand.Trim(), StringComparison.OrdinalIgnoreCase));
        var category = matchingBrand?.Category ?? "Other";

        // Generate forecast
        var forecastData = new List<object>();
        var seasonalFactorsApplied = new HashSet<string>();

        for (int d = 0; d < days; d++)
        {
            var date = forecastStart.AddDays(d);
            var seasonal = GetCategorySeasonalMultiplier(category, date.Month);

            if (Math.Abs(seasonal - 1.0) > 0.01)
                seasonalFactorsApplied.Add($"{date.ToString("MMM")} ({seasonal:F2}x)");

            var predicted = trailing30 * seasonal * (1.0 + trendSlope * d);
            predicted = Math.Max(1.0, predicted);
            var upper = Math.Round(predicted * 1.15, 1);
            var lower = Math.Round(predicted * 0.85, 1);

            forecastData.Add(new
            {
                date = date.ToString("yyyy-MM-dd"),
                predicted_volume = Math.Round(predicted, 1),
                confidence_upper = upper,
                confidence_lower = lower
            });
        }

        return new
        {
            brand = matchingBrand?.Name ?? brand,
            region = region ?? "all regions",
            forecast_period = new
            {
                start = forecastStart.ToString("yyyy-MM-dd"),
                end = forecastStart.AddDays(days - 1).ToString("yyyy-MM-dd"),
                days
            },
            algorithm = new
            {
                method = "trailing_average_with_seasonal_adjustment",
                trailing_30day_avg = Math.Round(trailing30, 1),
                trend_slope_per_day = Math.Round(trendSlope, 6),
                trend_direction = trendSlope > 0.0005 ? "upward" : trendSlope < -0.0005 ? "downward" : "flat",
                category,
                seasonal_factors_applied = seasonalFactorsApplied.OrderBy(s => s).ToArray()
            },
            confidence_band = "±15%",
            forecast = forecastData
        };
    }

    public object GetSeasonalityFactors(string? category)
    {
        using var conn = OpenConnection();
        conn.Open();

        using var cmd = conn.CreateCommand();
        if (!string.IsNullOrWhiteSpace(category))
        {
            cmd.CommandText = """
                SELECT Category, Month, Multiplier, EventName, Description
                FROM SeasonalFactors
                WHERE Category LIKE @category
                ORDER BY Category, Month
                """;
            cmd.Parameters.AddWithValue("@category", $"%{category.Trim()}%");
        }
        else
        {
            cmd.CommandText = """
                SELECT Category, Month, Multiplier, EventName, Description
                FROM SeasonalFactors
                ORDER BY Category, Month
                """;
        }

        var factors = new List<object>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            factors.Add(new
            {
                category = reader.GetString(0),
                month = reader.GetInt32(1),
                month_name = CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(reader.GetInt32(1)),
                multiplier = reader.GetDouble(2),
                event_name = reader.IsDBNull(3) ? null : reader.GetString(3),
                description = reader.IsDBNull(4) ? null : reader.GetString(4),
                impact = reader.GetDouble(2) switch
                {
                    > 1.2 => "strong_boost",
                    > 1.05 => "moderate_boost",
                    < 0.85 => "significant_decline",
                    < 0.95 => "moderate_decline",
                    _ => "baseline"
                }
            });
        }

        if (factors.Count == 0 && !string.IsNullOrWhiteSpace(category))
            return new { error = $"No seasonal factors found for category '{category}'.", available_categories = GetAvailableCategories() };

        var categories = factors.Select(f => ((dynamic)f).category as string).Distinct().ToArray();
        return new
        {
            categories,
            total_factors = factors.Count,
            factors
        };
    }

    public object IdentifyDemandRisks(string? brand, string? region = null)
    {
        using var conn = OpenConnection();
        conn.Open();

        // Get last 90 days of daily aggregated data
        var endDate = new DateOnly(2026, 5, 13);
        var startDate = endDate.AddDays(-89);

        using var cmd = conn.CreateCommand();
        var sql = new StringBuilder("""
            SELECT Brand, Region, Date, SUM(Volume) as DayVolume
            FROM DemandHistory
            WHERE Date >= @start AND Date <= @end
            """);
        cmd.Parameters.AddWithValue("@start", startDate.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("@end", endDate.ToString("yyyy-MM-dd"));

        if (!string.IsNullOrWhiteSpace(brand))
        {
            sql.Append(" AND Brand LIKE @brand");
            cmd.Parameters.AddWithValue("@brand", $"%{brand.Trim()}%");
        }
        if (!string.IsNullOrWhiteSpace(region))
        {
            sql.Append(" AND Region LIKE @region");
            cmd.Parameters.AddWithValue("@region", $"%{region.Trim()}%");
        }
        sql.Append(" GROUP BY Brand, Region, Date ORDER BY Brand, Region, Date");
        cmd.CommandText = sql.ToString();

        // Load data grouped by brand+region
        var seriesData = new Dictionary<string, List<(DateOnly Date, double Volume)>>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                var b = reader.GetString(0);
                var r = reader.GetString(1);
                var key = $"{b}|{r}";
                var date = DateOnly.Parse(reader.GetString(2));
                var vol = reader.GetDouble(3);

                if (!seriesData.ContainsKey(key))
                    seriesData[key] = [];
                seriesData[key].Add((date, vol));
            }
        }

        var risks = new List<object>();

        foreach (var (key, points) in seriesData)
        {
            var parts = key.Split('|');
            var brandName = parts[0];
            var regionName = parts[1];

            if (points.Count < 14) continue;

            // Calculate rolling 7-day averages
            var rollingAvgs = new List<(DateOnly Date, double Avg)>();
            for (int i = 6; i < points.Count; i++)
            {
                var avg = points.Skip(i - 6).Take(7).Average(p => p.Volume);
                rollingAvgs.Add((points[i].Date, avg));
            }

            // Detect sudden drops (>20% week-over-week)
            for (int i = 7; i < rollingAvgs.Count; i++)
            {
                var prevAvg = rollingAvgs[i - 7].Avg;
                var curAvg = rollingAvgs[i].Avg;
                if (prevAvg > 0)
                {
                    var change = (curAvg - prevAvg) / prevAvg;
                    if (change < -0.20)
                    {
                        risks.Add(new
                        {
                            brand = brandName,
                            region = regionName,
                            risk_type = "sudden_drop",
                            severity = change < -0.35 ? "high" : "medium",
                            change_percent = Math.Round(change * 100, 1),
                            description = $"Demand dropped {Math.Round(Math.Abs(change) * 100, 0)}% in a 7-day window",
                            affected_period = new { start = rollingAvgs[i - 7].Date.ToString("yyyy-MM-dd"), end = rollingAvgs[i].Date.ToString("yyyy-MM-dd") }
                        });
                        break; // One drop risk per series
                    }
                }
            }

            // Detect unusual spikes (>30% above trailing average)
            for (int i = 7; i < rollingAvgs.Count; i++)
            {
                var prevAvg = rollingAvgs[i - 7].Avg;
                var curAvg = rollingAvgs[i].Avg;
                if (prevAvg > 0)
                {
                    var change = (curAvg - prevAvg) / prevAvg;
                    if (change > 0.30)
                    {
                        risks.Add(new
                        {
                            brand = brandName,
                            region = regionName,
                            risk_type = "unusual_spike",
                            severity = change > 0.50 ? "high" : "medium",
                            change_percent = Math.Round(change * 100, 1),
                            description = $"Demand spiked {Math.Round(change * 100, 0)}% above trailing average — may indicate unsustainable surge or data anomaly",
                            affected_period = new { start = rollingAvgs[i - 7].Date.ToString("yyyy-MM-dd"), end = rollingAvgs[i].Date.ToString("yyyy-MM-dd") }
                        });
                        break;
                    }
                }
            }

            // Detect trend reversal (first half vs second half of 90 days)
            if (rollingAvgs.Count >= 30)
            {
                var firstHalf = rollingAvgs.Take(rollingAvgs.Count / 2).Average(r => r.Avg);
                var secondHalf = rollingAvgs.Skip(rollingAvgs.Count / 2).Average(r => r.Avg);
                if (firstHalf > 0)
                {
                    var trendShift = (secondHalf - firstHalf) / firstHalf;
                    if (Math.Abs(trendShift) > 0.15)
                    {
                        var direction = trendShift > 0 ? "upward" : "downward";
                        risks.Add(new
                        {
                            brand = brandName,
                            region = regionName,
                            risk_type = "trend_reversal",
                            severity = Math.Abs(trendShift) > 0.25 ? "high" : trendShift < 0 ? "medium" : "low",
                            change_percent = Math.Round(trendShift * 100, 1),
                            description = $"Significant {direction} trend shift of {Math.Round(Math.Abs(trendShift) * 100, 0)}% between first and second half of 90-day window",
                            affected_period = new
                            {
                                start = rollingAvgs[0].Date.ToString("yyyy-MM-dd"),
                                end = rollingAvgs[^1].Date.ToString("yyyy-MM-dd")
                            }
                        });
                    }
                }
            }
        }

        // Sort risks by severity
        var severityOrder = new Dictionary<string, int> { ["high"] = 0, ["medium"] = 1, ["low"] = 2 };
        var sortedRisks = risks
            .OrderBy(r => severityOrder.GetValueOrDefault((string)((dynamic)r).severity, 3))
            .ThenBy(r => ((dynamic)r).brand as string)
            .ToList();

        return new
        {
            analysis_period = new { start = startDate.ToString("yyyy-MM-dd"), end = endDate.ToString("yyyy-MM-dd"), days = 90 },
            filters = new { brand = brand ?? "all", region = region ?? "all" },
            total_risks = sortedRisks.Count,
            risk_summary = new
            {
                high = sortedRisks.Count(r => ((dynamic)r).severity == "high"),
                medium = sortedRisks.Count(r => ((dynamic)r).severity == "medium"),
                low = sortedRisks.Count(r => ((dynamic)r).severity == "low")
            },
            risks = sortedRisks
        };
    }

    private string[] GetAvailableCategories() =>
        _tenant.Brands.Select(b => b.Category).Distinct().ToArray();

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

    // ── Promo Seeding ────────────────────────────────────────────────────

    private static readonly string[] PromoTypes = ["BOGO", "Discount", "Display", "Digital", "Bundle"];
    private static readonly string[] SuccessRatings = ["Excellent", "Good", "Average", "Below Average", "Poor"];

    private void SeedPromoHistory(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO PromoHistory (Brand, Region, PromoType, CampaignName, StartDate, EndDate, Spend, BaselineVolume, ActualVolume, LiftPercent, ROI, SuccessRating)
            VALUES (@brand, @region, @type, @name, @start, @end, @spend, @baseline, @actual, @lift, @roi, @rating)
            """;

        var pBrand = cmd.Parameters.Add("@brand", SqliteType.Text);
        var pRegion = cmd.Parameters.Add("@region", SqliteType.Text);
        var pType = cmd.Parameters.Add("@type", SqliteType.Text);
        var pName = cmd.Parameters.Add("@name", SqliteType.Text);
        var pStart = cmd.Parameters.Add("@start", SqliteType.Text);
        var pEnd = cmd.Parameters.Add("@end", SqliteType.Text);
        var pSpend = cmd.Parameters.Add("@spend", SqliteType.Real);
        var pBaseline = cmd.Parameters.Add("@baseline", SqliteType.Real);
        var pActual = cmd.Parameters.Add("@actual", SqliteType.Real);
        var pLift = cmd.Parameters.Add("@lift", SqliteType.Real);
        var pRoi = cmd.Parameters.Add("@roi", SqliteType.Real);
        var pRating = cmd.Parameters.Add("@rating", SqliteType.Text);

        var baseDate = new DateOnly(2025, 1, 1);

        foreach (var brand in _tenant.Brands)
        {
            foreach (var region in _tenant.Regions)
            {
                var regionSeed = GetStableHash($"promo|{brand.Name}|{region}");
                var rng = new Random(regionSeed);

                for (int c = 0; c < 5; c++)
                {
                    var promoType = PromoTypes[rng.Next(PromoTypes.Length)];
                    var campaignStart = baseDate.AddDays(rng.Next(0, 300));
                    var durationDays = rng.Next(7, 45);
                    var campaignEnd = campaignStart.AddDays(durationDays);

                    var spend = Math.Round(5000 + rng.NextDouble() * 195000, 2);
                    var baselineVolume = Math.Round(1000 + rng.NextDouble() * 9000, 0);

                    var baseLift = promoType switch
                    {
                        "BOGO" => 15.0 + rng.NextDouble() * 25.0,
                        "Discount" => 8.0 + rng.NextDouble() * 18.0,
                        "Display" => 5.0 + rng.NextDouble() * 12.0,
                        "Digital" => 3.0 + rng.NextDouble() * 10.0,
                        "Bundle" => 10.0 + rng.NextDouble() * 20.0,
                        _ => 5.0 + rng.NextDouble() * 10.0
                    };

                    var liftPercent = Math.Round(baseLift, 1);
                    var actualVolume = Math.Round(baselineVolume * (1.0 + liftPercent / 100.0), 0);
                    var incrementalRevenue = (actualVolume - baselineVolume) * (5.0 + rng.NextDouble() * 15.0);
                    var roi = Math.Round((incrementalRevenue - spend) / spend * 100.0, 1);

                    var ratingIndex = roi switch
                    {
                        > 100 => 0,
                        > 50 => 1,
                        > 0 => 2,
                        > -30 => 3,
                        _ => 4
                    };

                    pBrand.Value = brand.Name;
                    pRegion.Value = region;
                    pType.Value = promoType;
                    pName.Value = $"{brand.Name} {promoType} {campaignStart:MMMyy}";
                    pStart.Value = campaignStart.ToString("yyyy-MM-dd");
                    pEnd.Value = campaignEnd.ToString("yyyy-MM-dd");
                    pSpend.Value = spend;
                    pBaseline.Value = baselineVolume;
                    pActual.Value = actualVolume;
                    pLift.Value = liftPercent;
                    pRoi.Value = roi;
                    pRating.Value = SuccessRatings[ratingIndex];
                    cmd.ExecuteNonQuery();
                }
            }
        }
    }

    private void SeedLiftCoefficients(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO LiftCoefficients (Category, PromoType, AvgLiftPercent, StdDev, MinSpend, MaxEffectiveSpend)
            VALUES (@cat, @type, @lift, @std, @min, @max)
            """;

        var pCat = cmd.Parameters.Add("@cat", SqliteType.Text);
        var pType = cmd.Parameters.Add("@type", SqliteType.Text);
        var pLift = cmd.Parameters.Add("@lift", SqliteType.Real);
        var pStd = cmd.Parameters.Add("@std", SqliteType.Real);
        var pMin = cmd.Parameters.Add("@min", SqliteType.Real);
        var pMax = cmd.Parameters.Add("@max", SqliteType.Real);

        var categories = _tenant.Brands.Select(b => b.Category).Distinct();

        foreach (var category in categories)
        {
            var catSeed = GetStableHash($"lift|{category}");
            var rng = new Random(catSeed);

            foreach (var promoType in PromoTypes)
            {
                var avgLift = promoType switch
                {
                    "BOGO" => 22.0 + rng.NextDouble() * 8.0,
                    "Discount" => 14.0 + rng.NextDouble() * 6.0,
                    "Display" => 8.0 + rng.NextDouble() * 5.0,
                    "Digital" => 5.0 + rng.NextDouble() * 4.0,
                    "Bundle" => 16.0 + rng.NextDouble() * 6.0,
                    _ => 10.0
                };

                var stdDev = Math.Round(avgLift * (0.15 + rng.NextDouble() * 0.25), 2);

                pCat.Value = category;
                pType.Value = promoType;
                pLift.Value = Math.Round(avgLift, 2);
                pStd.Value = stdDev;
                pMin.Value = Math.Round(1000 + rng.NextDouble() * 4000, 0);
                pMax.Value = Math.Round(150000 + rng.NextDouble() * 350000, 0);
                cmd.ExecuteNonQuery();
            }
        }
    }

    // ── Competitive Intelligence Seeding ─────────────────────────────────

    private static readonly Dictionary<string, string[]> CompetitorsByCategory = new()
    {
        ["Spirits"] = ["Jack Daniel's", "Maker's Mark", "Patrón", "Grey Goose", "Tito's"],
        ["Grocery"] = ["Kroger", "Whole Foods", "Trader Joe's", "Aldi", "Safeway"],
        ["Quick-Serve Restaurant"] = ["McDonald's", "Chick-fil-A", "Chipotle", "Taco Bell", "Wendy's"],
        ["Home Improvement"] = ["Home Depot", "Lowe's", "Menards", "Ace Hardware", "True Value"],
        ["Office Supply"] = ["Staples", "Office Depot", "Amazon Business", "Costco Business"],
        ["Furniture"] = ["IKEA", "Wayfair", "Ashley Furniture", "Rooms To Go", "Pottery Barn"]
    };

    private static readonly string[] PricingSources = ["web_scrape", "field_report", "syndicated"];
    private static readonly string[] ShareSources = ["Nielsen", "IRI", "internal_estimate", "syndicated"];
    private static readonly string[] ActivityTypes = ["price_drop", "new_product", "promo_launch", "distribution_change"];
    private static readonly string[] ImpactLevels = ["high", "medium", "low"];

    private void SeedCompetitorPricing(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO CompetitorPricing (Competitor, Brand, Category, Region, Price, PreviousPrice, PriceChangePercent, EffectiveDate, Source)
            VALUES (@competitor, @brand, @category, @region, @price, @prevPrice, @pctChange, @date, @source)
            """;

        var pCompetitor = cmd.Parameters.Add("@competitor", SqliteType.Text);
        var pBrand = cmd.Parameters.Add("@brand", SqliteType.Text);
        var pCategory = cmd.Parameters.Add("@category", SqliteType.Text);
        var pRegion = cmd.Parameters.Add("@region", SqliteType.Text);
        var pPrice = cmd.Parameters.Add("@price", SqliteType.Real);
        var pPrevPrice = cmd.Parameters.Add("@prevPrice", SqliteType.Real);
        var pPctChange = cmd.Parameters.Add("@pctChange", SqliteType.Real);
        var pDate = cmd.Parameters.Add("@date", SqliteType.Text);
        var pSource = cmd.Parameters.Add("@source", SqliteType.Text);

        var baseDate = new DateOnly(2025, 6, 1);

        foreach (var brand in _tenant.Brands)
        {
            var competitors = CompetitorsByCategory.GetValueOrDefault(brand.Category, ["Generic Competitor A", "Generic Competitor B", "Generic Competitor C"]);
            var basePrice = brand.PriceSegment == "Premium" ? 45.0 : 25.0;

            foreach (var competitor in competitors)
            {
                foreach (var region in _tenant.Regions)
                {
                    var seed = GetStableHash($"comprice|{competitor}|{brand.Name}|{region}");
                    var rng = new Random(seed);

                    // Generate 8-12 pricing records over time
                    var recordCount = 8 + rng.Next(5);
                    var competitorBase = basePrice * (0.85 + rng.NextDouble() * 0.35);

                    for (int i = 0; i < recordCount; i++)
                    {
                        var date = baseDate.AddDays(i * 30 + rng.Next(15));
                        var priceVariation = competitorBase * (0.90 + rng.NextDouble() * 0.20);
                        var previousPrice = i == 0 ? competitorBase : competitorBase * (0.92 + rng.NextDouble() * 0.16);
                        var pctChange = previousPrice > 0 ? Math.Round((priceVariation - previousPrice) / previousPrice * 100, 1) : 0;

                        // ~15% chance of dramatic price drop (>10%)
                        if (rng.NextDouble() < 0.15)
                        {
                            priceVariation = previousPrice * (0.80 + rng.NextDouble() * 0.08);
                            pctChange = Math.Round((priceVariation - previousPrice) / previousPrice * 100, 1);
                        }

                        pCompetitor.Value = competitor;
                        pBrand.Value = brand.Name;
                        pCategory.Value = brand.Category;
                        pRegion.Value = region;
                        pPrice.Value = Math.Round(priceVariation, 2);
                        pPrevPrice.Value = Math.Round(previousPrice, 2);
                        pPctChange.Value = pctChange;
                        pDate.Value = date.ToString("yyyy-MM-dd");
                        pSource.Value = PricingSources[rng.Next(PricingSources.Length)];
                        cmd.ExecuteNonQuery();

                        competitorBase = priceVariation;
                    }
                }
            }
        }
    }

    private void SeedMarketShare(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO MarketShare (Brand, Category, Region, Period, SharePercent, PreviousSharePercent, ShareChangePoints, Source)
            VALUES (@brand, @category, @region, @period, @share, @prevShare, @change, @source)
            """;

        var pBrand = cmd.Parameters.Add("@brand", SqliteType.Text);
        var pCategory = cmd.Parameters.Add("@category", SqliteType.Text);
        var pRegion = cmd.Parameters.Add("@region", SqliteType.Text);
        var pPeriod = cmd.Parameters.Add("@period", SqliteType.Text);
        var pShare = cmd.Parameters.Add("@share", SqliteType.Real);
        var pPrevShare = cmd.Parameters.Add("@prevShare", SqliteType.Real);
        var pChange = cmd.Parameters.Add("@change", SqliteType.Real);
        var pSource = cmd.Parameters.Add("@source", SqliteType.Text);

        // 6 quarters of data
        var quarters = new[] { "2025-Q1", "2025-Q2", "2025-Q3", "2025-Q4", "2026-Q1", "2026-Q2" };

        foreach (var brand in _tenant.Brands)
        {
            var competitors = CompetitorsByCategory.GetValueOrDefault(brand.Category, ["Generic Competitor A", "Generic Competitor B"]);
            var allPlayers = new List<string> { brand.Name };
            allPlayers.AddRange(competitors);

            foreach (var region in _tenant.Regions)
            {
                var seed = GetStableHash($"share|{brand.Category}|{region}");
                var rng = new Random(seed);

                // Allocate base shares — our brand gets 15-35%, competitors split the rest
                var ourBaseShare = 15.0 + rng.NextDouble() * 20.0;
                var remainingShare = 100.0 - ourBaseShare;
                var competitorShares = new double[competitors.Length];
                double totalComp = 0;
                for (int c = 0; c < competitors.Length; c++)
                {
                    competitorShares[c] = 5 + rng.NextDouble() * 25;
                    totalComp += competitorShares[c];
                }
                // Normalize competitor shares
                for (int c = 0; c < competitors.Length; c++)
                    competitorShares[c] = competitorShares[c] / totalComp * remainingShare;

                double prevOurShare = ourBaseShare;
                var prevCompShares = (double[])competitorShares.Clone();

                for (int q = 0; q < quarters.Length; q++)
                {
                    // Our brand: slight random walk
                    var drift = (rng.NextDouble() - 0.48) * 3.0;  // slight positive bias
                    var currentShare = Math.Round(Math.Max(5, Math.Min(50, prevOurShare + drift)), 1);
                    var changePoints = Math.Round(currentShare - prevOurShare, 1);

                    pBrand.Value = brand.Name;
                    pCategory.Value = brand.Category;
                    pRegion.Value = region;
                    pPeriod.Value = quarters[q];
                    pShare.Value = currentShare;
                    pPrevShare.Value = Math.Round(prevOurShare, 1);
                    pChange.Value = changePoints;
                    pSource.Value = ShareSources[rng.Next(ShareSources.Length)];
                    cmd.ExecuteNonQuery();

                    prevOurShare = currentShare;

                    // Competitors
                    for (int c = 0; c < competitors.Length; c++)
                    {
                        var compDrift = (rng.NextDouble() - 0.52) * 3.0;
                        var compShare = Math.Round(Math.Max(3, Math.Min(45, prevCompShares[c] + compDrift)), 1);
                        var compChange = Math.Round(compShare - prevCompShares[c], 1);

                        pBrand.Value = competitors[c];
                        pCategory.Value = brand.Category;
                        pRegion.Value = region;
                        pPeriod.Value = quarters[q];
                        pShare.Value = compShare;
                        pPrevShare.Value = Math.Round(prevCompShares[c], 1);
                        pChange.Value = compChange;
                        pSource.Value = ShareSources[rng.Next(ShareSources.Length)];
                        cmd.ExecuteNonQuery();

                        prevCompShares[c] = compShare;
                    }
                }
            }
        }
    }

    private void SeedCompetitorActivity(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO CompetitorActivity (Competitor, ActivityType, Category, Region, Description, Impact, DetectedDate, ResponseRecommendation)
            VALUES (@competitor, @type, @category, @region, @desc, @impact, @date, @recommendation)
            """;

        var pCompetitor = cmd.Parameters.Add("@competitor", SqliteType.Text);
        var pType = cmd.Parameters.Add("@type", SqliteType.Text);
        var pCategory = cmd.Parameters.Add("@category", SqliteType.Text);
        var pRegion = cmd.Parameters.Add("@region", SqliteType.Text);
        var pDesc = cmd.Parameters.Add("@desc", SqliteType.Text);
        var pImpact = cmd.Parameters.Add("@impact", SqliteType.Text);
        var pDate = cmd.Parameters.Add("@date", SqliteType.Text);
        var pRecommendation = cmd.Parameters.Add("@recommendation", SqliteType.Text);

        var baseDate = new DateOnly(2025, 8, 1);
        var activityTemplates = new (string type, string descTemplate, string recTemplate)[]
        {
            ("price_drop", "{0} dropped prices by {1}% on {2} products in {3}", "MATCH — Consider matching price within 2 weeks to avoid share loss"),
            ("price_drop", "{0} launched aggressive pricing on premium {2} line in {3}", "DIFFERENTIATE — Emphasize quality/heritage vs price competition"),
            ("new_product", "{0} launched new {2} product line targeting {3} market", "PREEMPT — Accelerate our own product pipeline to maintain innovation lead"),
            ("new_product", "{0} introduced value-tier {2} option in {3}", "IGNORE — Different segment, minimal overlap with our premium positioning"),
            ("promo_launch", "{0} started BOGO promotion on {2} in {3}", "MATCH — Launch counter-promotion within the same window"),
            ("promo_launch", "{0} launched loyalty program for {2} in {3}", "DIFFERENTIATE — Focus on product quality rather than loyalty discounts"),
            ("distribution_change", "{0} expanded {2} distribution to 200+ new stores in {3}", "PREEMPT — Secure additional shelf space before competitor gains foothold"),
            ("distribution_change", "{0} partnered with major retailer for exclusive {2} placement in {3}", "MATCH — Negotiate similar exclusive deals with competing retailers"),
        };

        var seed = GetStableHash("competitive_activity_seed");
        var rng = new Random(seed);

        foreach (var brand in _tenant.Brands)
        {
            var competitors = CompetitorsByCategory.GetValueOrDefault(brand.Category, ["Generic Competitor A", "Generic Competitor B"]);

            // 3-5 activities per category
            var activityCount = 3 + rng.Next(3);
            for (int i = 0; i < activityCount; i++)
            {
                var competitor = competitors[rng.Next(competitors.Length)];
                var template = activityTemplates[rng.Next(activityTemplates.Length)];
                var region = _tenant.Regions[rng.Next(_tenant.Regions.Count)];
                var priceDrop = 8 + rng.Next(20);
                var date = baseDate.AddDays(rng.Next(300));

                pCompetitor.Value = competitor;
                pType.Value = template.type;
                pCategory.Value = brand.Category;
                pRegion.Value = region;
                pDesc.Value = string.Format(template.descTemplate, competitor, priceDrop, brand.Category, region);
                pImpact.Value = ImpactLevels[rng.Next(ImpactLevels.Length)];
                pDate.Value = date.ToString("yyyy-MM-dd");
                pRecommendation.Value = template.recTemplate;
                cmd.ExecuteNonQuery();
            }
        }
    }

    // ── Promo Query Methods ────────────────────────────────────────────────

    public object GetPromoHistory(string? brand, string? region, string? promoType, int months = 18)
    {
        months = Math.Clamp(months, 1, 24);
        var cutoff = DateOnly.FromDateTime(DateTime.Today).AddMonths(-months);

        using var conn = OpenConnection();
        conn.Open();

        var where = new List<string> { "StartDate >= @cutoff" };
        using var cmd = conn.CreateCommand();
        cmd.Parameters.AddWithValue("@cutoff", cutoff.ToString("yyyy-MM-dd"));

        if (!string.IsNullOrWhiteSpace(brand))
        {
            where.Add("Brand LIKE @brand");
            cmd.Parameters.AddWithValue("@brand", $"%{brand.Trim()}%");
        }
        if (!string.IsNullOrWhiteSpace(region))
        {
            where.Add("Region LIKE @region");
            cmd.Parameters.AddWithValue("@region", $"%{region.Trim()}%");
        }
        if (!string.IsNullOrWhiteSpace(promoType))
        {
            where.Add("PromoType LIKE @type");
            cmd.Parameters.AddWithValue("@type", $"%{promoType.Trim()}%");
        }

        cmd.CommandText = $"""
            SELECT Brand, Region, PromoType, CampaignName, StartDate, EndDate,
                   Spend, BaselineVolume, ActualVolume, LiftPercent, ROI, SuccessRating
            FROM PromoHistory
            WHERE {string.Join(" AND ", where)}
            ORDER BY StartDate DESC
            """;

        var campaigns = new List<object>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            campaigns.Add(new
            {
                brand = reader.GetString(0),
                region = reader.GetString(1),
                promo_type = reader.GetString(2),
                campaign_name = reader.GetString(3),
                start_date = reader.GetString(4),
                end_date = reader.GetString(5),
                spend = reader.GetDouble(6),
                baseline_volume = reader.GetDouble(7),
                actual_volume = reader.GetDouble(8),
                lift_percent = reader.GetDouble(9),
                roi = reader.GetDouble(10),
                success_rating = reader.GetString(11)
            });
        }

        return new
        {
            filters = new { brand = brand ?? "all", region = region ?? "all", promo_type = promoType ?? "all", months },
            total_campaigns = campaigns.Count,
            campaigns
        };
    }

    public object CalculateLift(string brand, string region, string promoType, double spend)
    {
        if (string.IsNullOrWhiteSpace(brand))
            return new { error = "Parameter 'brand' is required.", available_brands = GetAvailableBrands() };
        if (string.IsNullOrWhiteSpace(region))
            return new { error = "Parameter 'region' is required.", available_regions = GetAvailableRegions() };
        if (string.IsNullOrWhiteSpace(promoType))
            return new { error = "Parameter 'promoType' is required.", available_types = PromoTypes };

        var brandConfig = _tenant.Brands.FirstOrDefault(b =>
            b.Name.Contains(brand.Trim(), StringComparison.OrdinalIgnoreCase));
        if (brandConfig == null)
            return new { error = $"Unknown brand '{brand}'.", available_brands = GetAvailableBrands() };

        using var conn = OpenConnection();
        conn.Open();

        using var coeffCmd = conn.CreateCommand();
        coeffCmd.CommandText = """
            SELECT AvgLiftPercent, StdDev, MinSpend, MaxEffectiveSpend
            FROM LiftCoefficients
            WHERE Category = @cat AND PromoType LIKE @type
            LIMIT 1
            """;
        coeffCmd.Parameters.AddWithValue("@cat", brandConfig.Category);
        coeffCmd.Parameters.AddWithValue("@type", $"%{promoType.Trim()}%");

        using var reader = coeffCmd.ExecuteReader();
        if (!reader.Read())
            return new { error = $"No lift data for category '{brandConfig.Category}' and promo type '{promoType}'." };

        var avgLift = reader.GetDouble(0);
        var stdDev = reader.GetDouble(1);
        var minSpend = reader.GetDouble(2);
        var maxEffective = reader.GetDouble(3);
        reader.Close();

        using var countCmd = conn.CreateCommand();
        countCmd.CommandText = """
            SELECT COUNT(*) FROM PromoHistory
            WHERE Brand LIKE @brand AND Region LIKE @region AND PromoType LIKE @type
            """;
        countCmd.Parameters.AddWithValue("@brand", $"%{brand.Trim()}%");
        countCmd.Parameters.AddWithValue("@region", $"%{region.Trim()}%");
        countCmd.Parameters.AddWithValue("@type", $"%{promoType.Trim()}%");
        var similarCount = Convert.ToInt32(countCmd.ExecuteScalar());

        var spendEfficiency = spend <= maxEffective ? 1.0 : maxEffective / spend;
        var expectedLift = Math.Round(avgLift * spendEfficiency, 2);
        var confidence = Math.Round(Math.Max(0.3, 1.0 - (stdDev / avgLift)), 2);

        return new
        {
            brand = brandConfig.Name,
            region,
            category = brandConfig.Category,
            promo_type = promoType.Trim(),
            spend,
            expected_lift_percent = expectedLift,
            confidence,
            similar_campaigns = similarCount,
            diminishing_returns = spend > maxEffective,
            spend_efficiency = Math.Round(spendEfficiency * 100, 1),
            coefficient_details = new { avg_lift = avgLift, std_dev = stdDev, min_spend = minSpend, max_effective_spend = maxEffective }
        };
    }

    public object EvaluateTiming(string brand, string region, DateOnly start, DateOnly end)
    {
        if (string.IsNullOrWhiteSpace(brand))
            return new { error = "Parameter 'brand' is required.", available_brands = GetAvailableBrands() };
        if (string.IsNullOrWhiteSpace(region))
            return new { error = "Parameter 'region' is required.", available_regions = GetAvailableRegions() };
        if (end <= start)
            return new { error = "End date must be after start date." };

        using var conn = OpenConnection();
        conn.Open();

        using var overlapCmd = conn.CreateCommand();
        overlapCmd.CommandText = """
            SELECT CampaignName, PromoType, StartDate, EndDate
            FROM PromoHistory
            WHERE Brand LIKE @brand AND Region LIKE @region
            AND StartDate <= @end AND EndDate >= @start
            ORDER BY StartDate
            """;
        overlapCmd.Parameters.AddWithValue("@brand", $"%{brand.Trim()}%");
        overlapCmd.Parameters.AddWithValue("@region", $"%{region.Trim()}%");
        overlapCmd.Parameters.AddWithValue("@start", start.ToString("yyyy-MM-dd"));
        overlapCmd.Parameters.AddWithValue("@end", end.ToString("yyyy-MM-dd"));

        var conflicts = new List<object>();
        using (var reader = overlapCmd.ExecuteReader())
        {
            while (reader.Read())
                conflicts.Add(new { campaign = reader.GetString(0), promo_type = reader.GetString(1), start_date = reader.GetString(2), end_date = reader.GetString(3) });
        }

        using var recentCmd = conn.CreateCommand();
        var lookbackStart = start.AddDays(-60);
        recentCmd.CommandText = """
            SELECT CampaignName, PromoType, EndDate
            FROM PromoHistory
            WHERE Brand LIKE @brand AND Region LIKE @region
            AND EndDate >= @lookback AND EndDate < @start
            ORDER BY EndDate DESC
            """;
        recentCmd.Parameters.AddWithValue("@brand", $"%{brand.Trim()}%");
        recentCmd.Parameters.AddWithValue("@region", $"%{region.Trim()}%");
        recentCmd.Parameters.AddWithValue("@lookback", lookbackStart.ToString("yyyy-MM-dd"));
        recentCmd.Parameters.AddWithValue("@start", start.ToString("yyyy-MM-dd"));

        var recentPromos = new List<object>();
        using (var reader = recentCmd.ExecuteReader())
        {
            while (reader.Read())
                recentPromos.Add(new { campaign = reader.GetString(0), promo_type = reader.GetString(1), ended = reader.GetString(2) });
        }

        var brandConfig = _tenant.Brands.FirstOrDefault(b =>
            b.Name.Contains(brand.Trim(), StringComparison.OrdinalIgnoreCase));
        var seasonalityScore = GetSeasonalityScore(conn, brandConfig?.Category, start.Month);

        var proximityPenalty = recentPromos.Count > 0 ? Math.Min(recentPromos.Count * 0.15, 0.50) : 0.0;
        var timingScore = Math.Max(0.0, Math.Min(1.0,
            seasonalityScore - proximityPenalty - (conflicts.Count > 0 ? 0.30 : 0.0)));
        timingScore = Math.Round(timingScore, 2);

        return new
        {
            proposed_period = new { start = start.ToString("yyyy-MM-dd"), end = end.ToString("yyyy-MM-dd") },
            timing_score = timingScore,
            has_conflicts = conflicts.Count > 0,
            conflicts,
            recent_similar_promos = recentPromos,
            proximity_penalty = proximityPenalty,
            seasonality_score = seasonalityScore,
            recommendation = timingScore >= 0.70 ? "Good timing" : timingScore >= 0.40 ? "Acceptable, review conflicts" : "Poor timing, consider rescheduling"
        };
    }

    private double GetSeasonalityScore(SqliteConnection conn, string? category, int month)
    {
        if (string.IsNullOrWhiteSpace(category)) return 0.6;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Multiplier FROM SeasonalFactors WHERE Category = @cat AND Month = @month LIMIT 1";
        cmd.Parameters.AddWithValue("@cat", category);
        cmd.Parameters.AddWithValue("@month", month);
        var result = cmd.ExecuteScalar();
        if (result is null or DBNull) return 0.6;
        return Math.Min(1.0, Math.Max(0.2, Convert.ToDouble(result) / 1.5));
    }

    public object EstimateROI(string brand, string region, string promoType, double spend, int durationWeeks)
    {
        if (string.IsNullOrWhiteSpace(brand))
            return new { error = "Parameter 'brand' is required.", available_brands = GetAvailableBrands() };
        if (string.IsNullOrWhiteSpace(region))
            return new { error = "Parameter 'region' is required.", available_regions = GetAvailableRegions() };
        if (spend <= 0)
            return new { error = "Parameter 'spend' must be positive." };
        if (durationWeeks is < 1 or > 12)
            return new { error = "Parameter 'durationWeeks' must be between 1 and 12." };

        var brandConfig = _tenant.Brands.FirstOrDefault(b =>
            b.Name.Contains(brand.Trim(), StringComparison.OrdinalIgnoreCase));
        if (brandConfig == null)
            return new { error = $"Unknown brand '{brand}'.", available_brands = GetAvailableBrands() };

        using var conn = OpenConnection();
        conn.Open();

        double avgLift = 10.0, stdDev = 5.0;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT AvgLiftPercent, StdDev FROM LiftCoefficients WHERE Category = @cat AND PromoType LIKE @type LIMIT 1";
            cmd.Parameters.AddWithValue("@cat", brandConfig.Category);
            cmd.Parameters.AddWithValue("@type", $"%{promoType.Trim()}%");
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                avgLift = reader.GetDouble(0);
                stdDev = reader.GetDouble(1);
            }
        }

        double baselineVolume = 5000;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT AVG(BaselineVolume) FROM PromoHistory WHERE Brand LIKE @brand AND Region LIKE @region";
            cmd.Parameters.AddWithValue("@brand", $"%{brand.Trim()}%");
            cmd.Parameters.AddWithValue("@region", $"%{region.Trim()}%");
            var result = cmd.ExecuteScalar();
            if (result is not null and not DBNull)
                baselineVolume = Convert.ToDouble(result);
        }

        var weeklyBaseline = baselineVolume / 4.0;
        var totalBaseline = weeklyBaseline * durationWeeks;

        var incrementalUnits = totalBaseline * (avgLift / 100.0);
        var revenuePerUnit = 8.0 + (brandConfig.Category == "Spirits" ? 12.0 : brandConfig.Category == "Grocery" ? 2.0 : 5.0);
        var incrementalRevenue = incrementalUnits * revenuePerUnit;
        var expectedRoi = Math.Round((incrementalRevenue - spend) / spend * 100.0, 2);

        var lowerLift = Math.Max(0, avgLift - 1.96 * stdDev);
        var upperLift = avgLift + 1.96 * stdDev;

        var lowerRoi = Math.Round((totalBaseline * (lowerLift / 100.0) * revenuePerUnit - spend) / spend * 100.0, 2);
        var upperRoi = Math.Round((totalBaseline * (upperLift / 100.0) * revenuePerUnit - spend) / spend * 100.0, 2);

        var breakeven = Math.Round(spend / revenuePerUnit, 0);
        var varianceFactor = Math.Round(stdDev / Math.Max(avgLift, 1.0), 2);

        return new
        {
            brand = brandConfig.Name,
            region,
            promo_type = promoType.Trim(),
            duration_weeks = durationWeeks,
            inputs = new { spend, expected_lift_percent = avgLift, baseline_volume = totalBaseline, revenue_per_unit = revenuePerUnit },
            roi = new { expected = expectedRoi, lower_bound = lowerRoi, upper_bound = upperRoi, confidence_interval_width = Math.Round(upperRoi - lowerRoi, 2) },
            incremental = new { units = Math.Round(incrementalUnits, 0), revenue = Math.Round(incrementalRevenue, 2) },
            breakeven_units = breakeven,
            variance_factor = varianceFactor,
            is_positive_roi = expectedRoi > 0,
            requires_approval = spend > 500000,
            risk_level = varianceFactor > 0.5 ? "high" : varianceFactor > 0.3 ? "medium" : "low"
        };
    }

    public object GetPromoCalendar(string? brand = null, string? region = null, int months = 6)
    {
        months = Math.Clamp(months, 1, 24);
        var cutoff = DateOnly.FromDateTime(DateTime.Today).AddMonths(-months);

        using var conn = OpenConnection();
        conn.Open();

        var where = new List<string> { "StartDate >= @cutoff" };
        using var cmd = conn.CreateCommand();
        cmd.Parameters.AddWithValue("@cutoff", cutoff.ToString("yyyy-MM-dd"));

        if (!string.IsNullOrWhiteSpace(brand))
        {
            where.Add("Brand LIKE @brand");
            cmd.Parameters.AddWithValue("@brand", $"%{brand.Trim()}%");
        }
        if (!string.IsNullOrWhiteSpace(region))
        {
            where.Add("Region LIKE @region");
            cmd.Parameters.AddWithValue("@region", $"%{region.Trim()}%");
        }

        cmd.CommandText = $"""
            SELECT Brand, Region, PromoType, CampaignName, StartDate, EndDate, Spend, ROI
            FROM PromoHistory
            WHERE {string.Join(" AND ", where)}
            ORDER BY StartDate ASC
            """;

        var events = new List<object>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            events.Add(new
            {
                brand = reader.GetString(0),
                region = reader.GetString(1),
                promo_type = reader.GetString(2),
                campaign = reader.GetString(3),
                start_date = reader.GetString(4),
                end_date = reader.GetString(5),
                spend = reader.GetDouble(6),
                roi = reader.GetDouble(7)
            });
        }

        return new
        {
            filters = new { brand = brand ?? "all", region = region ?? "all", months },
            total_events = events.Count,
            calendar = events
        };
    }

    public static object GetPromoTypes() => new
    {
        promo_types = new[]
        {
            new { code = "bogo", name = "Buy One Get One", description = "BOGO promotions offering free or discounted additional units" },
            new { code = "discount", name = "Discount", description = "Price-off promotions, typically 10-30% off regular price" },
            new { code = "display", name = "Display", description = "In-store display and end-cap placement promotions" },
            new { code = "digital", name = "Digital", description = "Digital/online promotions including social media and email campaigns" },
            new { code = "bundle", name = "Bundle", description = "Product bundling promotions combining related items at a discount" }
        }
    };

    // ── Competitive Intelligence Queries ─────────────────────────────────

    public object GetCompetitorPricing(string? brand = null, string? category = null, string? region = null, string? competitors = null)
    {
        using var conn = OpenConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();

        var filters = new List<string>();
        if (!string.IsNullOrWhiteSpace(brand))
        {
            filters.Add("Brand LIKE @brand");
            cmd.Parameters.AddWithValue("@brand", $"%{brand}%");
        }
        if (!string.IsNullOrWhiteSpace(category))
        {
            filters.Add("Category LIKE @category");
            cmd.Parameters.AddWithValue("@category", $"%{category}%");
        }
        if (!string.IsNullOrWhiteSpace(region))
        {
            filters.Add("Region LIKE @region");
            cmd.Parameters.AddWithValue("@region", $"%{region}%");
        }
        if (!string.IsNullOrWhiteSpace(competitors))
        {
            var compList = competitors.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var compFilters = new List<string>();
            for (int i = 0; i < compList.Length; i++)
            {
                compFilters.Add($"Competitor LIKE @comp{i}");
                cmd.Parameters.AddWithValue($"@comp{i}", $"%{compList[i]}%");
            }
            filters.Add($"({string.Join(" OR ", compFilters)})");
        }

        var where = filters.Count > 0 ? $"WHERE {string.Join(" AND ", filters)}" : "";
        cmd.CommandText = $"""
            SELECT Competitor, Brand, Category, Region, Price, PreviousPrice, PriceChangePercent, EffectiveDate, Source
            FROM CompetitorPricing
            {where}
            ORDER BY EffectiveDate DESC
            LIMIT 200
            """;

        using var reader = cmd.ExecuteReader();
        var records = new List<object>();
        while (reader.Read())
        {
            records.Add(new
            {
                competitor = reader.GetString(0),
                brand = reader.GetString(1),
                category = reader.GetString(2),
                region = reader.GetString(3),
                price = reader.GetDouble(4),
                previous_price = reader.IsDBNull(5) ? (double?)null : reader.GetDouble(5),
                price_change_percent = reader.IsDBNull(6) ? (double?)null : reader.GetDouble(6),
                effective_date = reader.GetString(7),
                source = reader.IsDBNull(8) ? null : reader.GetString(8)
            });
        }

        // Identify dramatic price drops (>10%) as threats
        var threats = records.Where(r =>
        {
            var pct = ((dynamic)r).price_change_percent;
            return pct != null && (double)pct < -10;
        }).ToList();

        return new
        {
            filters = new { brand = brand ?? "all", category = category ?? "all", region = region ?? "all", competitors = competitors ?? "all" },
            total_records = records.Count,
            price_drop_threats = threats.Count,
            pricing = records
        };
    }

    public object GetMarketShare(string? brand = null, string? category = null, string? region = null, string? period = null)
    {
        using var conn = OpenConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();

        var filters = new List<string>();
        if (!string.IsNullOrWhiteSpace(brand))
        {
            filters.Add("Brand LIKE @brand");
            cmd.Parameters.AddWithValue("@brand", $"%{brand}%");
        }
        if (!string.IsNullOrWhiteSpace(category))
        {
            filters.Add("Category LIKE @category");
            cmd.Parameters.AddWithValue("@category", $"%{category}%");
        }
        if (!string.IsNullOrWhiteSpace(region))
        {
            filters.Add("Region LIKE @region");
            cmd.Parameters.AddWithValue("@region", $"%{region}%");
        }
        if (!string.IsNullOrWhiteSpace(period))
        {
            filters.Add("Period LIKE @period");
            cmd.Parameters.AddWithValue("@period", $"%{period}%");
        }

        var where = filters.Count > 0 ? $"WHERE {string.Join(" AND ", filters)}" : "";
        cmd.CommandText = $"""
            SELECT Brand, Category, Region, Period, SharePercent, PreviousSharePercent, ShareChangePoints, Source
            FROM MarketShare
            {where}
            ORDER BY Period DESC, SharePercent DESC
            LIMIT 300
            """;

        using var reader = cmd.ExecuteReader();
        var records = new List<object>();
        while (reader.Read())
        {
            records.Add(new
            {
                brand = reader.GetString(0),
                category = reader.GetString(1),
                region = reader.GetString(2),
                period = reader.GetString(3),
                share_percent = reader.GetDouble(4),
                previous_share_percent = reader.IsDBNull(5) ? (double?)null : reader.GetDouble(5),
                share_change_points = reader.IsDBNull(6) ? (double?)null : reader.GetDouble(6),
                source = reader.IsDBNull(7) ? null : reader.GetString(7)
            });
        }

        // Find significant share losses (>2 points)
        var shareLosses = records.Where(r =>
        {
            var change = ((dynamic)r).share_change_points;
            return change != null && (double)change < -2.0;
        }).ToList();

        return new
        {
            filters = new { brand = brand ?? "all", category = category ?? "all", region = region ?? "all", period = period ?? "all" },
            total_records = records.Count,
            significant_share_losses = shareLosses.Count,
            share_data = records
        };
    }

    public object DetectCompetitiveThreats(string? brand = null, string? category = null, string? region = null)
    {
        var threats = new List<object>();

        // 1. Price drop threats (competitor dropped >10%)
        using var conn = OpenConnection();
        conn.Open();

        var pricingFilters = new List<string> { "PriceChangePercent < -10" };
        using var priceCmd = conn.CreateCommand();
        if (!string.IsNullOrWhiteSpace(brand))
        {
            pricingFilters.Add("Brand LIKE @brand");
            priceCmd.Parameters.AddWithValue("@brand", $"%{brand}%");
        }
        if (!string.IsNullOrWhiteSpace(category))
        {
            pricingFilters.Add("Category LIKE @category");
            priceCmd.Parameters.AddWithValue("@category", $"%{category}%");
        }
        if (!string.IsNullOrWhiteSpace(region))
        {
            pricingFilters.Add("Region LIKE @region");
            priceCmd.Parameters.AddWithValue("@region", $"%{region}%");
        }

        priceCmd.CommandText = $"""
            SELECT Competitor, Brand, Category, Region, Price, PreviousPrice, PriceChangePercent, EffectiveDate
            FROM CompetitorPricing
            WHERE {string.Join(" AND ", pricingFilters)}
            ORDER BY PriceChangePercent ASC
            LIMIT 20
            """;

        using var priceReader = priceCmd.ExecuteReader();
        while (priceReader.Read())
        {
            var pctChange = priceReader.GetDouble(6);
            var severity = pctChange < -20 ? "high" : "medium";
            var recommendation = pctChange < -20 ? "MATCH" : "DIFFERENTIATE";
            var reasoning = pctChange < -20
                ? "Significant price undercut threatens market share. Consider matching within 1-2 weeks."
                : "Moderate price drop. Differentiate on value proposition rather than matching.";

            threats.Add(new
            {
                type = "price_drop",
                severity,
                competitor = priceReader.GetString(0),
                brand = priceReader.GetString(1),
                category = priceReader.GetString(2),
                region = priceReader.GetString(3),
                details = new { current_price = priceReader.GetDouble(4), previous_price = priceReader.GetDouble(5), change_percent = pctChange },
                detected_date = priceReader.GetString(7),
                recommendation,
                reasoning,
                historical_success_rate = recommendation == "MATCH" ? "72% effective in similar situations" : "65% effective in similar situations"
            });
        }

        // 2. Market share threats (>2 point drop)
        var shareFilters = new List<string> { "ShareChangePoints < -2" };
        using var shareCmd = conn.CreateCommand();
        if (!string.IsNullOrWhiteSpace(brand))
        {
            shareFilters.Add("Brand LIKE @brand");
            shareCmd.Parameters.AddWithValue("@brand", $"%{brand}%");
        }
        if (!string.IsNullOrWhiteSpace(category))
        {
            shareFilters.Add("Category LIKE @category");
            shareCmd.Parameters.AddWithValue("@category", $"%{category}%");
        }
        if (!string.IsNullOrWhiteSpace(region))
        {
            shareFilters.Add("Region LIKE @region");
            shareCmd.Parameters.AddWithValue("@region", $"%{region}%");
        }

        shareCmd.CommandText = $"""
            SELECT Brand, Category, Region, Period, SharePercent, PreviousSharePercent, ShareChangePoints
            FROM MarketShare
            WHERE {string.Join(" AND ", shareFilters)}
            ORDER BY ShareChangePoints ASC
            LIMIT 20
            """;

        using var shareReader = shareCmd.ExecuteReader();
        while (shareReader.Read())
        {
            var changePoints = shareReader.GetDouble(6);
            var severity = changePoints < -4 ? "high" : "medium";

            threats.Add(new
            {
                type = "share_loss",
                severity,
                competitor = (string?)null,
                brand = shareReader.GetString(0),
                category = shareReader.GetString(1),
                region = shareReader.GetString(2),
                details = new { period = shareReader.GetString(3), current_share = shareReader.GetDouble(4), previous_share = shareReader.GetDouble(5), change_points = changePoints },
                detected_date = shareReader.GetString(3),
                recommendation = severity == "high" ? "PREEMPT" : "MATCH",
                reasoning = severity == "high"
                    ? "Significant share erosion detected. Launch counter-offensive with targeted promotions and distribution push."
                    : "Moderate share loss. Increase competitive monitoring and prepare defensive promotions.",
                historical_success_rate = severity == "high" ? "58% effective in similar situations" : "67% effective in similar situations"
            });
        }

        // 3. Competitive activity threats
        var actFilters = new List<string> { "Impact = 'high'" };
        using var actCmd = conn.CreateCommand();
        if (!string.IsNullOrWhiteSpace(category))
        {
            actFilters.Add("Category LIKE @category");
            actCmd.Parameters.AddWithValue("@category", $"%{category}%");
        }
        if (!string.IsNullOrWhiteSpace(region))
        {
            actFilters.Add("Region LIKE @region");
            actCmd.Parameters.AddWithValue("@region", $"%{region}%");
        }

        actCmd.CommandText = $"""
            SELECT Competitor, ActivityType, Category, Region, Description, Impact, DetectedDate, ResponseRecommendation
            FROM CompetitorActivity
            WHERE {string.Join(" AND ", actFilters)}
            ORDER BY DetectedDate DESC
            LIMIT 15
            """;

        using var actReader = actCmd.ExecuteReader();
        while (actReader.Read())
        {
            var recText = actReader.IsDBNull(7) ? "IGNORE" : actReader.GetString(7);
            var recommendation = recText.Contains("MATCH") ? "MATCH"
                : recText.Contains("PREEMPT") ? "PREEMPT"
                : recText.Contains("DIFFERENTIATE") ? "DIFFERENTIATE"
                : "IGNORE";

            threats.Add(new
            {
                type = actReader.GetString(1),
                severity = "high",
                competitor = actReader.GetString(0),
                brand = (string?)null,
                category = actReader.GetString(2),
                region = actReader.GetString(3),
                details = new { description = actReader.IsDBNull(4) ? "" : actReader.GetString(4) },
                detected_date = actReader.GetString(6),
                recommendation,
                reasoning = recText,
                historical_success_rate = recommendation == "MATCH" ? "72% effective" : recommendation == "PREEMPT" ? "58% effective" : "65% effective"
            });
        }

        return new
        {
            filters = new { brand = brand ?? "all", category = category ?? "all", region = region ?? "all" },
            total_threats = threats.Count,
            high_severity = threats.Count(t => ((dynamic)t).severity == "high"),
            medium_severity = threats.Count(t => ((dynamic)t).severity == "medium"),
            threats
        };
    }

    public object GetCompetitiveLandscape(string category, string region)
    {
        if (string.IsNullOrWhiteSpace(category))
            return new { error = "Parameter 'category' is required." };
        if (string.IsNullOrWhiteSpace(region))
            return new { error = "Parameter 'region' is required." };

        using var conn = OpenConnection();
        conn.Open();

        // Get latest market share for all players in this category/region
        using var shareCmd = conn.CreateCommand();
        shareCmd.CommandText = """
            SELECT Brand, SharePercent, PreviousSharePercent, ShareChangePoints, Period
            FROM MarketShare
            WHERE Category LIKE @category AND Region LIKE @region
            AND Period = (SELECT MAX(Period) FROM MarketShare WHERE Category LIKE @category AND Region LIKE @region)
            ORDER BY SharePercent DESC
            """;
        shareCmd.Parameters.AddWithValue("@category", $"%{category}%");
        shareCmd.Parameters.AddWithValue("@region", $"%{region}%");

        var players = new List<object>();
        string? latestPeriod = null;
        using var shareReader = shareCmd.ExecuteReader();
        while (shareReader.Read())
        {
            latestPeriod ??= shareReader.GetString(4);
            players.Add(new
            {
                brand = shareReader.GetString(0),
                share_percent = shareReader.GetDouble(1),
                previous_share = shareReader.IsDBNull(2) ? (double?)null : shareReader.GetDouble(2),
                share_change = shareReader.IsDBNull(3) ? (double?)null : shareReader.GetDouble(3),
                trend = shareReader.IsDBNull(3) ? "stable"
                    : shareReader.GetDouble(3) > 0.5 ? "gaining"
                    : shareReader.GetDouble(3) < -0.5 ? "losing"
                    : "stable"
            });
        }

        // Recent competitive activities in this category/region
        using var actCmd = conn.CreateCommand();
        actCmd.CommandText = """
            SELECT Competitor, ActivityType, Description, Impact, DetectedDate, ResponseRecommendation
            FROM CompetitorActivity
            WHERE Category LIKE @category AND Region LIKE @region
            ORDER BY DetectedDate DESC
            LIMIT 10
            """;
        actCmd.Parameters.AddWithValue("@category", $"%{category}%");
        actCmd.Parameters.AddWithValue("@region", $"%{region}%");

        var activities = new List<object>();
        using var actReader = actCmd.ExecuteReader();
        while (actReader.Read())
        {
            activities.Add(new
            {
                competitor = actReader.GetString(0),
                type = actReader.GetString(1),
                description = actReader.IsDBNull(2) ? "" : actReader.GetString(2),
                impact = actReader.IsDBNull(3) ? "low" : actReader.GetString(3),
                date = actReader.GetString(4),
                recommendation = actReader.IsDBNull(5) ? null : actReader.GetString(5)
            });
        }

        // Recent pricing moves
        using var priceCmd = conn.CreateCommand();
        priceCmd.CommandText = """
            SELECT Competitor, Brand, Price, PreviousPrice, PriceChangePercent, EffectiveDate
            FROM CompetitorPricing
            WHERE Category LIKE @category AND Region LIKE @region
            ORDER BY EffectiveDate DESC
            LIMIT 20
            """;
        priceCmd.Parameters.AddWithValue("@category", $"%{category}%");
        priceCmd.Parameters.AddWithValue("@region", $"%{region}%");

        var pricingMoves = new List<object>();
        using var priceReader = priceCmd.ExecuteReader();
        while (priceReader.Read())
        {
            pricingMoves.Add(new
            {
                competitor = priceReader.GetString(0),
                brand = priceReader.GetString(1),
                price = priceReader.GetDouble(2),
                previous_price = priceReader.IsDBNull(3) ? (double?)null : priceReader.GetDouble(3),
                change_percent = priceReader.IsDBNull(4) ? (double?)null : priceReader.GetDouble(4),
                date = priceReader.GetString(5)
            });
        }

        // Identify our position
        var ourBrands = _tenant.Brands
            .Where(b => b.Category.Equals(category, StringComparison.OrdinalIgnoreCase))
            .Select(b => b.Name)
            .ToList();

        var ourPosition = players.Where(p => ourBrands.Contains(((dynamic)p).brand as string, StringComparer.OrdinalIgnoreCase)).ToList();
        var competitorPositions = players.Where(p => !ourBrands.Contains(((dynamic)p).brand as string, StringComparer.OrdinalIgnoreCase)).ToList();

        return new
        {
            category,
            region,
            period = latestPeriod ?? "unknown",
            our_brands = ourPosition,
            competitors = competitorPositions,
            total_players = players.Count,
            recent_activities = activities,
            pricing_moves = pricingMoves
        };
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
