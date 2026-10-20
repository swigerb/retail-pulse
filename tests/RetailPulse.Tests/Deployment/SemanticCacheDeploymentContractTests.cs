using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;

namespace RetailPulse.Tests.Deployment;

/// <summary>
/// Issue #338 — opt-in Azure Managed Redis vector cache for APIM semantic caching.
/// Assertions run against the ARM template compiled from the Bicep sources (no live Azure deployment validation).
/// </summary>
public sealed class SemanticCacheDeploymentContractTests : IDisposable
{
    private readonly string _repoRoot = FindRepoRoot();
    private readonly List<string> _tempFiles = [];

    public void Dispose()
    {
        foreach (string file in _tempFiles.Where(File.Exists))
            File.Delete(file);
    }

    [Fact]
    public void CompiledMain_SkipsModuleWhenDisabledAndEmitsOnlyNonSecretOutputs()
    {
        JsonElement? main = Compile(Path.Combine("infra", "main.bicep"));
        if (main is null) return;

        JsonElement root = main.Value;
        root.GetProperty("parameters").GetProperty("semanticCacheEnabled").GetProperty("defaultValue").GetBoolean().Should().BeFalse();
        root.GetProperty("parameters").GetProperty("semanticCachePublicNetworkAccess").GetProperty("defaultValue").GetString().Should().Be("Disabled");

        JsonElement module = root.GetProperty("resources").GetProperty("semanticCache");
        module.GetProperty("condition").GetString().Should().Contain("semanticCacheEnabled");
        module.GetProperty("type").GetString().Should().Be("Microsoft.Resources/deployments");

        string[] outputs = [.. root.GetProperty("outputs").EnumerateObject().Select(o => o.Name).Where(n => n.StartsWith("AZURE_SEMANTIC_CACHE_", StringComparison.Ordinal))];
        outputs.Should().BeEquivalentTo("AZURE_SEMANTIC_CACHE_ENABLED", "AZURE_SEMANTIC_CACHE_NAME",
            "AZURE_SEMANTIC_CACHE_RESOURCE_ID", "AZURE_SEMANTIC_CACHE_HOST", "AZURE_SEMANTIC_CACHE_TTL_SECONDS");
        module.GetRawText().Should().NotContainEquivalentOf("listKeys").And.NotContainEquivalentOf("connectionString");
    }

    [Fact]
    public void CompiledModule_DeclaresRedisSearchDatabaseWithAccessKeyAuthAndNoIdentityAssignment()
    {
        JsonElement? module = Compile(Path.Combine("infra", "modules", "managed-redis-cache.bicep"));
        if (module is null) return;

        JsonElement[] resources = [.. module.Value.GetProperty("resources").EnumerateArray()];
        JsonElement cache = resources.Single(r => r.GetProperty("type").GetString() == "Microsoft.Cache/redisEnterprise");
        cache.GetProperty("sku").GetProperty("name").GetString().Should().Contain("skuName");
        cache.GetProperty("properties").GetProperty("minimumTlsVersion").GetString().Should().Be("1.2");
        cache.GetProperty("properties").GetProperty("publicNetworkAccess").GetString().Should().Contain("validatedPublicNetworkAccess");
        cache.TryGetProperty("identity", out _).Should().BeFalse("APIM cannot authenticate with Entra, so no cache identity is needed");

        JsonElement database = resources.Single(r => r.GetProperty("type").GetString() == "Microsoft.Cache/redisEnterprise/databases");
        JsonElement props = database.GetProperty("properties");
        props.GetProperty("accessKeysAuthentication").GetString().Should().Be("Enabled",
            "APIM semantic caching supports only Redis connection strings with access keys");
        props.GetProperty("clientProtocol").GetString().Should().Be("Encrypted");
        props.GetProperty("clusteringPolicy").GetString().Should().Be("EnterpriseCluster");
        props.GetProperty("evictionPolicy").GetString().Should().Be("NoEviction");
        props.GetProperty("modules").EnumerateArray().Select(m => m.GetProperty("name").GetString()).Should().Equal("RediSearch");

        resources.Select(r => r.GetProperty("type").GetString()).Should().NotContain("Microsoft.Cache/redisEnterprise/databases/accessPolicyAssignments");
        module.Value.GetProperty("parameters").TryGetProperty("apimPrincipalId", out _).Should().BeFalse();
        module.Value.GetProperty("parameters").GetProperty("skuName").GetProperty("defaultValue").GetString().Should().Be("Balanced_B0");
        module.Value.GetProperty("parameters").GetProperty("publicNetworkAccess").GetProperty("defaultValue").GetString().Should().Be("Disabled");
    }

    [Fact]
    public void CompiledModule_FailsClosedWithoutPrivateEndpointAndWiresPrivateDns()
    {
        JsonElement? module = Compile(Path.Combine("infra", "modules", "managed-redis-cache.bicep"));
        if (module is null) return;

        JsonElement variables = module.Value.GetProperty("variables");
        string valid = variables.GetProperty("networkPostureValid").GetString()!;
        valid.Should().Contain("privateEndpointSubnetId").And.Contain("privateDnsZoneId");
        string guard = variables.GetProperty("validatedPublicNetworkAccess").GetString()!;
        guard.Should().Contain("json(").And.Contain("networkPostureValid");

        const string Subnet = "/subscriptions/s/subnets/pe";
        const string Zone = "/subscriptions/s/privateDnsZones/privatelink.redis.azure.net";
        string Evaluate(string access, string subnet, string zone) =>
            ArmExpression.Evaluate(variables, "validatedPublicNetworkAccess", new Dictionary<string, object?>
            {
                ["publicNetworkAccess"] = access,
                ["privateEndpointSubnetId"] = subnet,
                ["privateDnsZoneId"] = zone,
            });

        Evaluate("Enabled", "", "").Should().Be("Enabled");
        Evaluate("Disabled", Subnet, Zone).Should().Be("Disabled");
        foreach ((string subnet, string zone) in new[] { ("", ""), (Subnet, ""), ("", Zone) })
        {
            FluentActions.Invoking(() => Evaluate("Disabled", subnet, zone)).Should().Throw<JsonException>(
                "an incomplete private posture must evaluate to invalid JSON, which ARM rejects before creating the cache");
        }

        JsonElement[] resources = [.. module.Value.GetProperty("resources").EnumerateArray()];
        JsonElement endpoint = resources.Single(r => r.GetProperty("type").GetString() == "Microsoft.Network/privateEndpoints");
        endpoint.GetProperty("condition").GetString().Should().Contain("usePrivateEndpoint");
        endpoint.GetProperty("properties").GetProperty("privateLinkServiceConnections")[0].GetProperty("properties")
            .GetProperty("groupIds")[0].GetString().Should().Be("redisEnterprise");

        JsonElement zoneGroup = resources.Single(r => r.GetProperty("type").GetString() == "Microsoft.Network/privateEndpoints/privateDnsZoneGroups");
        zoneGroup.GetProperty("condition").GetString().Should().Contain("usePrivateEndpoint");
        zoneGroup.GetRawText().Should().Contain("privateDnsZoneId");
    }

    [Fact]
    public void CompiledModule_ExposesNoSecretsOrKeyListing()
    {
        JsonElement? module = Compile(Path.Combine("infra", "modules", "managed-redis-cache.bicep"));
        if (module is null) return;

        module.Value.GetRawText().Should().NotContainEquivalentOf("listKeys")
            .And.NotContainEquivalentOf("primaryKey").And.NotContainEquivalentOf("connectionString");
        foreach (JsonProperty output in module.Value.GetProperty("outputs").EnumerateObject())
            output.Name.ToLowerInvariant().Should().NotContainAny("key", "password", "secret", "connectionstring");
    }

    [Fact]
    public void Docs_StateAuthCostNetworkAndTeardownTruthfully()
    {
        string d = File.ReadAllText(Path.Combine(_repoRoot, "docs", "deployment-azd.md"));
        d.Should().Contain("Optional semantic cache").And.Contain("Cost class").And.Contain("az redisenterprise delete");
        d.Should().Contain("does not support Microsoft Entra");
        d.Should().Contain("AZURE_SEMANTIC_CACHE_PRIVATE_DNS_ZONE_ID");
        d.Should().Contain("advisory handoff value only").And.Contain("issue #339");
        d.Should().NotContain("access keys disabled").And.NotContain("APIM system identity is the only data principal");
    }

    private JsonElement? Compile(string relativeBicepPath)
    {
        string source = Path.Combine(_repoRoot, relativeBicepPath);
        string output = Path.Combine(Path.GetTempPath(), $"retailpulse-cache-{Guid.NewGuid():N}.json");
        _tempFiles.Add(output);

        try
        {
            if (!Run(["bicep", "install"], out _)) return Unavailable();
            if (!Run(["bicep", "build", "--file", source, "--outfile", output], out string log))
            {
                return log.Contains("not found", StringComparison.OrdinalIgnoreCase)
                    ? Unavailable()
                    : throw new InvalidOperationException($"az bicep build failed for {relativeBicepPath}:\n{log}");
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return Unavailable();
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(output));
        return doc.RootElement.Clone();
    }

    private static JsonElement? Unavailable()
    {
        Environment.GetEnvironmentVariable("CI").Should().NotBe("true", "CI must compile the Bicep sources");
        return null;
    }

    private static bool Run(string[] args, out string output)
    {
        ProcessStartInfo info = OperatingSystem.IsWindows()
            ? new("cmd.exe") { ArgumentList = { "/c", "az" } }
            : new("az");
        foreach (string a in args) info.ArgumentList.Add(a);
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.UseShellExecute = false;
        info.Environment["DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"] = "1";
        using Process p = Process.Start(info)!;
        Task<string> err = p.StandardError.ReadToEndAsync();
        string o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        output = o + err.Result;
        return p.ExitCode == 0;
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "RetailPulse.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Repo root not found");
    }
}

/// <summary>
/// Minimal local evaluator for the ARM template-language subset used by the cache module's
/// network guard. It proves the compiled expression branches correctly; it is not ARM
/// deployment validation and does not contact Azure.
/// </summary>
internal static class ArmExpression
{
    public static string Evaluate(JsonElement variables, string name, IReadOnlyDictionary<string, object?> parameters)
    {
        object? value = EvaluateVariable(variables, name, parameters);
        return (string)value!;
    }

    private static object? EvaluateVariable(JsonElement variables, string name, IReadOnlyDictionary<string, object?> parameters)
    {
        string raw = variables.GetProperty(name).GetString()!;
        raw.Should().StartWith("[").And.EndWith("]");
        int pos = 1;
        object? result = Parse(raw, ref pos, variables, parameters);
        pos.Should().Be(raw.Length - 1, "the whole expression must be consumed");
        return result;
    }

    private static object? Parse(string s, ref int pos, JsonElement variables, IReadOnlyDictionary<string, object?> parameters)
    {
        if (s[pos] == '\'')
        {
            System.Text.StringBuilder text = new();
            pos++;
            while (true)
            {
                if (s[pos] == '\'' && pos + 1 < s.Length && s[pos + 1] == '\'') { text.Append('\''); pos += 2; }
                else if (s[pos] == '\'') { pos++; return text.ToString(); }
                else
                {
                    text.Append(s[pos++]);
                }
            }
        }

        int start = pos;
        while (char.IsLetterOrDigit(s[pos])) pos++;
        string fn = s[start..pos];
        s[pos].Should().Be('(');
        pos++;

        List<Func<object?>> args = [];
        while (s[pos] != ')')
        {
            int argStart = pos;
            Skip(s, ref pos);
            string argText = s[argStart..pos];
            args.Add(() =>
            {
                int p = 0;
                return Parse(argText, ref p, variables, parameters);
            });
            if (s[pos] == ',') pos++;
            while (s[pos] == ' ') pos++;
        }
        pos++;

        return fn switch
        {
            "parameters" => parameters[(string)args[0]()!],
            "variables" => EvaluateVariable(variables, (string)args[0]()!, parameters),
            "equals" => Equals(args[0](), args[1]()),
            "not" => !(bool)args[0]()!,
            "empty" => string.IsNullOrEmpty((string?)args[0]()),
            "or" => args.Any(a => (bool)a()!),
            "and" => args.All(a => (bool)a()!),
            "if" => (bool)args[0]()! ? args[1]() : args[2](),
            "json" => JsonDocument.Parse((string)args[0]()!).RootElement.GetRawText(),
            "string" => args[0]()?.ToString(),
            _ => throw new NotSupportedException($"ARM function '{fn}' is not supported by the test evaluator"),
        };
    }

    private static void Skip(string s, ref int pos)
    {
        if (s[pos] == '\'')
        {
            pos++;
            while (true)
            {
                if (s[pos] == '\'' && s[pos + 1] == '\'')
                {
                    pos += 2;
                }
                else if (s[pos] == '\'') { pos++; return; }
                else
                {
                    pos++;
                }
            }
        }

        while (char.IsLetterOrDigit(s[pos])) pos++;
        pos++;
        int depth = 1;
        while (depth > 0)
        {
            if (s[pos] == '\'') { Skip(s, ref pos); continue; }
            if (s[pos] == '(') depth++;
            else if (s[pos] == ')') depth--;
            pos++;
        }
    }
}
