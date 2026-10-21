using System.Xml.Linq;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Newtonsoft.Json.Linq;

// ---- APIM runtime stand-ins (just the surface the policy expressions use) -------------------------
public interface IBody { T As<T>(bool preserveContent = false) where T : class; }
public interface IResponse { int StatusCode { get; } IBody Body { get; } }
public sealed class Body(string text) : IBody
{
    public T As<T>(bool preserveContent = false) where T : class
        => (T)(object)JObject.Parse(text);
}
public sealed class FakeResponse(int status, string body) : IResponse
{
    public int StatusCode => status;
    public IBody Body { get; } = new Body(body);
}
public sealed class Vars
{
    public readonly Dictionary<string, object?> Map = new();
    public object? this[string k] => Map.TryGetValue(k, out var v) ? v : throw new KeyNotFoundException(k);
    public bool ContainsKey(string k) => Map.ContainsKey(k);
    public T? GetValueOrDefault<T>(string k) where T : class => Map.TryGetValue(k, out var v) ? v as T : null;
}
public sealed class Req { public string Method = "POST"; public IBody Body = new Body("{}"); }
public sealed class Resp { public int StatusCode = 200; public IBody Body = new Body("{}"); }
public sealed class Ctx { public Vars Variables = new(); public Req Request = new(); public Resp Response = new(); public Guid RequestId = Guid.NewGuid(); }
public sealed class Globals { public Ctx context = new(); }

public sealed record Cs(int? Status, string Body); // Status null => no response at all (timeout / DNS / TLS)
public sealed class Result
{
    public int Status; public string Body = ""; public bool ModelCalled; public Dictionary<string, string> Headers = new();
    public List<(string Url, string Body)> CsCalls = new();
    public string H(string n) => Headers.TryGetValue(n, out var v) ? v : "";
}

public static class Policy
{
    static readonly string Modules = FindModules();
    static string FindModules()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "RetailPulse.slnx"))) d = d.Parent;
        return Path.Combine(d!.FullName, "infra", "modules");
    }
    public static string Render(string file, string mode, int threshold) =>
        File.ReadAllText(Path.Combine(Modules, file)).Replace("{cs-endpoint}", "https://cs.example/").Replace("{safety-mode}", mode).Replace("{severity-threshold}", threshold.ToString());
    public static string Raw(string file) => File.ReadAllText(Path.Combine(Modules, file));

    static readonly Dictionary<string, ScriptRunner<object>> Cache = new();
    static readonly ScriptOptions Options = ScriptOptions.Default
        .AddReferences(typeof(JObject).Assembly, typeof(Ctx).Assembly, typeof(object).Assembly, typeof(System.Linq.Enumerable).Assembly, typeof(System.Collections.Generic.List<>).Assembly)
        .AddImports("System", "System.Linq", "System.Collections.Generic", "Newtonsoft.Json.Linq");

    public static int Compiled;
    public static object? Eval(string expr, Ctx ctx)
    {
        string code = expr.StartsWith("@{") ? expr[2..^1] : "return (" + expr[2..^1] + ");";
        // Wrapping in a method makes a missing `return` a hard compile error (CS0161) instead of a silent null.
        string script = "object F() {\n" + code + "\n}\nreturn F();";
        if (!Cache.TryGetValue(script, out var run)) { run = CSharpScript.Create<object>(script, Options, typeof(Globals)).CreateDelegate(); Cache[script] = run; Compiled++; }
        return run(new Globals { context = ctx }).GetAwaiter().GetResult();
    }
    public static bool IsExpr(string s) => s.StartsWith("@(") || s.StartsWith("@{");
    public static IEnumerable<string> AllExpressions(string fragment)
    {
        var doc = XDocument.Parse("<r>" + fragment + "</r>");
        foreach (var e in doc.Descendants())
        {
            foreach (var a in e.Attributes().Where(a => a.Name == "value" || a.Name == "condition" || a.Name == "code" || a.Name == "reason"))
                if (IsExpr(a.Value)) yield return a.Value;
            if (!e.HasElements && IsExpr(e.Value)) yield return e.Value;
        }
    }

    static string Str(string raw, Ctx c) => IsExpr(raw) ? Convert.ToString(Eval(raw, c))! : raw;

    // Interprets a fragment against ctx. Returns true when a return-response ended the pipeline.
    public static bool Run(IEnumerable<XElement> nodes, Ctx ctx, Func<string, string, Cs?> cs, bool tokenAvailable, Result res, Dictionary<string, string> setHeaders)
    {
        foreach (var n in nodes)
        {
            switch (n.Name.LocalName)
            {
                case "set-variable":
                    ctx.Variables.Map[(string)n.Attribute("name")!] = IsExpr((string)n.Attribute("value")!) ? Eval((string)n.Attribute("value")!, ctx) : (string)n.Attribute("value")!;
                    break;
                case "authentication-managed-identity":
                    if (tokenAvailable) ctx.Variables.Map[(string)n.Attribute("output-token-variable-name")!] = "tok";
                    break;
                case "choose":
                    foreach (var w in n.Elements("when"))
                        if ((bool)Eval((string)w.Attribute("condition")!, ctx)!) { if (Run(w.Elements(), ctx, cs, tokenAvailable, res, setHeaders)) return true; break; }
                    break;
                case "send-request":
                    string url = n.Element("set-url")!.Value, body = Str(n.Element("set-body")!.Value, ctx);
                    res.CsCalls.Add((url, body));
                    var r = cs(url, body);
                    if (r is { Status: not null }) ctx.Variables.Map[(string)n.Attribute("response-variable-name")!] = new FakeResponse(r.Status.Value, r.Body);
                    break;
                case "set-header":
                    setHeaders[(string)n.Attribute("name")!] = Str(n.Element("value")!.Value, ctx);
                    break;
                case "return-response":
                    res.Status = Convert.ToInt32(Str((string)n.Element("set-status")!.Attribute("code")!, ctx));
                    foreach (var h in n.Elements("set-header")) res.Headers[(string)h.Attribute("name")!] = Str(h.Element("value")!.Value, ctx);
                    res.Body = Str(n.Element("set-body")!.Value, ctx);
                    return true;
            }
        }
        return false;
    }
}

public sealed class Scenario
{
    public string Name = ""; public string Request = ""; public string Mode = "demo"; public int Threshold = 4; public string Method = "POST";
    public bool Token = true; public Func<string, string, Cs?> Cs = (_, _) => null;
    public int ModelStatus = 200; public string ModelBody = "";
    public bool InboundFragment = true;
}

public static class Program
{
    static string Chat(params (string role, string content)[] m) =>
        new JObject(new JProperty("messages", new JArray(m.Select(x => new JObject(new JProperty("role", x.role), new JProperty("content", x.content)))))).ToString(Newtonsoft.Json.Formatting.None);
    static string Out(string text) => new JObject(new JProperty("choices", new JArray(new JObject(new JProperty("message", new JObject(new JProperty("role", "assistant"), new JProperty("content", text))))))).ToString(Newtonsoft.Json.Formatting.None);
    static string Analyze(int sev) => $"{{\"categoriesAnalysis\":[{{\"category\":\"Hate\",\"severity\":0}},{{\"category\":\"SelfHarm\",\"severity\":0}},{{\"category\":\"Sexual\",\"severity\":0}},{{\"category\":\"Violence\",\"severity\":{sev}}}]}}";
    static string Shield(bool attack, int docs = 0) => "{\"userPromptAnalysis\":{\"attackDetected\":" + (attack ? "true" : "false") + "},\"documentsAnalysis\":[" + string.Join(",", Enumerable.Repeat("{\"attackDetected\":false}", docs)) + "]}";
    static Func<string, string, Cs?> Backend(Cs? shield, Cs? analyze) => (url, _) => url.Contains("shieldPrompt") ? shield : analyze;
    static readonly Cs OkShield = new(200, Shield(false)), OkAnalyze = new(200, Analyze(0));
    const string Secret = "SECRET-PAYLOAD-7731";
    static int failures, checks;

    static Result Execute(Scenario s)
    {
        var res = new Result();
        var ctx = new Ctx();
        ctx.Request.Method = s.Method;
        ctx.Request.Body = new Body(s.Request);
        var inH = new Dictionary<string, string>();
        var outH = new Dictionary<string, string>();
        if (s.InboundFragment)
        {
            var inbound = XDocument.Parse("<r>" + Policy.Render("apim-openai-safety-inbound.xml", s.Mode, s.Threshold) + "</r>").Root!.Elements();
            if (Policy.Run(inbound, ctx, s.Cs, s.Token, res, inH)) return res;
        }
        res.ModelCalled = true;
        res.Status = s.ModelStatus; res.Body = s.ModelBody;
        ctx.Response.StatusCode = s.ModelStatus; ctx.Response.Body = new Body(s.ModelBody.Length == 0 ? "{}" : s.ModelBody);
        if (s.InboundFragment)
        {
            var outbound = XDocument.Parse("<r>" + Policy.Render("apim-openai-safety-outbound.xml", s.Mode, s.Threshold) + "</r>").Root!.Elements();
            var r2 = new Result { ModelCalled = true, Status = s.ModelStatus, Body = s.ModelBody, CsCalls = res.CsCalls };
            if (Policy.Run(outbound, ctx, s.Cs, s.Token, r2, outH)) return r2;
            foreach (var kv in outH) r2.Headers[kv.Key] = kv.Value;
            return r2;
        }
        return res;
    }

    static void Check(string scenario, bool ok, string what)
    {
        checks++;
        if (!ok) { failures++; Console.WriteLine($"FAIL [{scenario}] {what}"); }
    }

    static void Expect(Scenario s, int status, string decision, string? outChecked = null, bool? modelCalled = null, string? safety = null, int? csCalls = null)
    {
        var r = Execute(s);
        Check(s.Name, r.Status == status, $"status {r.Status} != {status}");
        Check(s.Name, r.H("x-rp-gw-safety-decision") == decision, $"decision '{r.H("x-rp-gw-safety-decision")}' != '{decision}'");
        if (outChecked != null) Check(s.Name, r.H("x-rp-gw-safety-output-checked") == outChecked, $"output-checked '{r.H("x-rp-gw-safety-output-checked")}' != '{outChecked}'");
        if (modelCalled != null) Check(s.Name, r.ModelCalled == modelCalled, $"modelCalled {r.ModelCalled} != {modelCalled}");
        if (safety != null) Check(s.Name, r.H("x-rp-gw-safety") == safety, $"x-rp-gw-safety '{r.H("x-rp-gw-safety")}' != '{safety}'");
        if (csCalls != null) Check(s.Name, r.CsCalls.Count == csCalls, $"cs calls {r.CsCalls.Count} != {csCalls}");
        if (status >= 400 && !r.ModelCalled)
        {
            string wire = r.Body + string.Join("|", r.Headers.Select(h => h.Key + h.Value));
            Check(s.Name, !wire.Contains(Secret) && !wire.Contains("tok"), "payload/token leaked in blocked response");
            Check(s.Name, r.Body.Contains("gateway_safety_"), "blocked body must be the generic gateway error");
        }
        Console.WriteLine($"[{s.Name}] status={r.Status} decision={r.H("x-rp-gw-safety-decision")} outChecked={r.H("x-rp-gw-safety-output-checked")} model={r.ModelCalled}");
    }

    public static int Main(string[] args)
    {
        string benign = Chat(("system", "be helpful"), ("user", "What were Q3 sales for store 12?"));
        string ok200 = Out("Q3 sales were up 4%.");
        Scenario S(string n, string req, Cs? shield, Cs? analyze, Action<Scenario>? tweak = null)
        {
            var s = new Scenario { Name = n, Request = req, Cs = Backend(shield, analyze), ModelBody = ok200 };
            tweak?.Invoke(s); return s;
        }

        // 0. every expression compiles AND returns on all paths (rp-safety-ms etc.); evaluates the real XML
        foreach (var f in new[] { "apim-openai-safety-inbound.xml", "apim-openai-safety-outbound.xml" })
            foreach (var e in Policy.AllExpressions(Policy.Render(f, "demo", 4)))
            {
                try { Policy.Eval(e, new Ctx()); }
                catch (Microsoft.CodeAnalysis.Scripting.CompilationErrorException ce) { Check("compile", false, f + ": " + ce.Message); }
                catch (Exception) { /* runtime errors on an empty context are expected; compile errors are not */ }
            }
        Console.WriteLine($"[compile] {Policy.Compiled} distinct expressions compiled");

        // 1. benign
        Expect(S("benign", benign, OkShield, OkAnalyze), 200, "allow", "true", true, "passed", 3);
        Expect(S("benign:tool-docs", Chat(("user", "summarise"), ("tool", "inventory table")), new Cs(200, Shield(false, 1)), OkAnalyze), 200, "allow", "true", true, "passed");
        // 2. attack (user prompt and tool document)
        Expect(S("attack:user", Chat(("user", Secret)), new Cs(200, Shield(true)), OkAnalyze), 403, "block", "false", false, "blocked");
        Expect(S("attack:tool-doc", Chat(("user", "x"), ("tool", Secret)), new Cs(200, "{\"userPromptAnalysis\":{\"attackDetected\":false},\"documentsAnalysis\":[{\"attackDetected\":true}]}"), OkAnalyze), 403, "block", "false", false);
        Expect(S("attack:beats-dependency-failure", benign, new Cs(200, Shield(true)), new Cs(500, "")), 403, "block", "false", false);
        // 3. severity boundary (threshold 4): 2 allows, 4 and 6 block; threshold 2 blocks at 2
        Expect(S("severity:2-below", benign, OkShield, new Cs(200, Analyze(2))), 200, "allow", "true", true);
        Expect(S("severity:4-boundary", benign, OkShield, new Cs(200, Analyze(4))), 403, "block", "false", false);
        Expect(S("severity:6", benign, OkShield, new Cs(200, Analyze(6))), 403, "block", "false", false);
        Expect(S("severity:threshold2-at-2", benign, OkShield, new Cs(200, Analyze(2)), s => s.Threshold = 2), 403, "block", "false", false);
        // 4. oversized content is rejected, never truncated, and never sent anywhere
        string big(int n) => new string('a', n);
        Expect(S("oversized:user", Chat(("user", big(10001))), OkShield, OkAnalyze), 400, "block", "false", false, "blocked", 0);
        Expect(S("oversized:user-over-two-messages", Chat(("user", big(6000)), ("user", big(6000))), OkShield, OkAnalyze), 400, "block", "false", false, null, 0);
        Expect(S("rejected:tool-only-no-user-text", Chat(("tool", "inventory table")), OkShield, OkAnalyze), 400, "block", "false", false, null, 0);
        Expect(S("oversized:tool", Chat(("user", "x"), ("tool", big(10001))), OkShield, OkAnalyze), 400, "block", "false", false, null, 0);
        Expect(S("oversized:tool-total", Chat(("user", "x"), ("tool", big(6000)), ("tool", big(6000))), OkShield, OkAnalyze), 400, "block", "false", false, null, 0);
        Expect(S("oversized:6-tools", Chat(new[] { ("user", "x") }.Concat(Enumerable.Repeat(("tool", "t"), 6)).ToArray()), OkShield, OkAnalyze), 400, "block", "false", false, null, 0);
        var atLimit = Execute(S("limit:exactly-10000", Chat(("user", big(10000))), OkShield, OkAnalyze));
        Check("limit:exactly-10000", atLimit.Status == 200 && atLimit.CsCalls.Count >= 2 && JObject.Parse(atLimit.CsCalls[0].Body)["userPrompt"]!.ToString().Length == 10000 && JObject.Parse(atLimit.CsCalls[1].Body)["text"]!.ToString().Length == 10000, "10000-char content must be sent in full, untruncated");
        Expect(S("oversized:output", benign, OkShield, OkAnalyze, s => s.ModelBody = Out(big(10001))), 503, "block", "false", true, "blocked", 2);
        // 5. outbound block: model reply is withheld, evidence is generic
        var ob = S("outbound:block", benign, OkShield, new Cs(200, Analyze(0)), s => { s.ModelBody = Out(Secret); });
        ob.Cs = (u, b) => u.Contains("shieldPrompt") ? OkShield : (b.Contains(Secret) ? new Cs(200, Analyze(6)) : OkAnalyze);
        Expect(ob, 403, "block", "true", true, "blocked");
        Check("outbound:block", !Execute(ob).Body.Contains(Secret), "blocked output must not reach the client");
        // 6. streaming: input-only truth
        var st = Execute(S("streaming", "{\"stream\":true,\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}", OkShield, OkAnalyze));
        Check("streaming", st.Status == 200 && st.H("x-rp-gw-safety-output-checked") == "false" && st.H("x-rp-gw-safety") != "passed" && st.CsCalls.Count == 2, $"streaming must be input-only (status {st.Status}, checked '{st.H("x-rp-gw-safety-output-checked")}', safety '{st.H("x-rp-gw-safety")}', calls {st.CsCalls.Count})");
        Console.WriteLine("[streaming] input-only truth verified");
        // 7. confirmed 408/429/5xx: demo fail-open with evidence, regulated fail-closed
        foreach (int code in new[] { 408, 429, 500, 502, 503, 504 })
        {
            Expect(S($"demo:shield-{code}", benign, new Cs(code, ""), OkAnalyze), 200, "allowed_on_safety_error", "true", true);
            Expect(S($"demo:analyze-{code}", benign, OkShield, new Cs(code, "")), 200, "allowed_on_safety_error", "false", true);
            Expect(S($"demo:output-{code}", benign, OkShield, OkAnalyze, s => s.Cs = (u, b) => u.Contains("shieldPrompt") ? OkShield : (b.Contains("Q3 sales were") ? new Cs(code, "") : OkAnalyze)), 200, "allowed_on_safety_error", "false");
            Expect(S($"regulated:shield-{code}", benign, new Cs(code, ""), OkAnalyze, s => s.Mode = "regulated"), 503, "block", "false", false);
        }
        // 8. everything else fails closed (demo): null, DNS/TLS, malformed, missing fields, auth, config, no token
        var bad = new (string n, Cs? c)[] {
            ("null", null), ("garbage-200", new Cs(200, "not json")), ("empty-object-200", new Cs(200, "{}")), ("200-array", new Cs(200, "[]")),
            ("400", new Cs(400, "")), ("401", new Cs(401, "")), ("403", new Cs(403, "")), ("404", new Cs(404, "")), ("301", new Cs(301, "")), ("200-no-severity", new Cs(200, "{\"categoriesAnalysis\":[{\"category\":\"Hate\"}]}")),
            ("200-missing-categories", new Cs(200, "{\"categoriesAnalysis\":[{\"category\":\"Hate\",\"severity\":0}]}")) };
        foreach (var (n, c) in bad)
        {
            Expect(S($"closed:analyze-{n}", benign, OkShield, c), 503, "block", "false", false, "blocked");
            if (n is not ("200-no-severity" or "200-missing-categories")) Expect(S($"closed:shield-{n}", benign, c, OkAnalyze), 503, "block", "false", false, "blocked");
        }
        Expect(S("closed:shield-200-no-attack-flag", benign, new Cs(200, "{\"userPromptAnalysis\":{}}"), OkAnalyze), 503, "block", "false", false);
        Expect(S("closed:shield-doc-count-mismatch", Chat(("user", "x"), ("tool", "t")), new Cs(200, Shield(false, 0)), OkAnalyze), 503, "block", "false", false);
        Expect(S("closed:no-token", benign, OkShield, OkAnalyze, s => s.Token = false), 503, "block", "false", false, null, 0);
        Expect(S("closed:request-not-json", "<<not json>>", OkShield, OkAnalyze), 400, "block", "false", false, null, 0);
        Expect(S("closed:request-no-messages", "{\"input\":\"x\"}", OkShield, OkAnalyze), 400, "block", "false", false, null, 0);
        Expect(S("closed:request-bad-content", "{\"messages\":[{\"role\":\"user\",\"content\":{\"a\":1}}]}", OkShield, OkAnalyze), 400, "block", "false", false, null, 0);
        foreach (var (n, c) in bad.Append(("429-not-demo-class-401", new Cs(401, ""))))
        {
            if (n is "429-not-demo-class-401") continue;
            var o = S($"closed:output-{n}", benign, OkShield, OkAnalyze, s => s.Cs = (u, b) => u.Contains("shieldPrompt") ? OkShield : (b.Contains("Q3 sales were") ? c : OkAnalyze));
            Expect(o, 503, "block", "false", true, "blocked");
        }
        Expect(S("closed:output-not-json", benign, OkShield, OkAnalyze, s => s.ModelBody = "<html/>"), 503, "block", "false", true, "blocked");
        Expect(S("closed:output-no-choices", benign, OkShield, OkAnalyze, s => s.ModelBody = "{\"id\":\"x\"}"), 503, "block", "false", true, "blocked");
        // empty completion (e.g. tool-call only): nothing to analyse -> never claim checked/passed
        var emp = Execute(S("output:empty", benign, OkShield, OkAnalyze, s => s.ModelBody = Out("")));
        Check("output:empty", emp.Status == 200 && emp.H("x-rp-gw-safety-output-checked") == "false" && emp.H("x-rp-gw-safety") != "passed", "empty output must not be reported as checked/passed");
        // non-200 model responses are not analysed and never claim output-checked
        var m500 = Execute(S("output:model-500", benign, OkShield, OkAnalyze, s => { s.ModelStatus = 500; s.ModelBody = "{}"; }));
        Check("output:model-500", m500.H("x-rp-gw-safety-output-checked") == "false" && m500.H("x-rp-gw-safety") != "passed", "non-200 must not claim output checked");
        // 9. no payload leakage anywhere in evidence
        var leak = Execute(S("leak", Chat(("user", Secret)), OkShield, OkAnalyze, s => s.ModelBody = Out(Secret)));
        Check("leak", !string.Join("|", leak.Headers.Select(h => h.Key + "=" + h.Value)).Contains(Secret), "payload leaked into evidence headers");
        // 10. disabled mode: base policy minus fragments has no safety runtime at all and still strips spoofed headers
        string baseline = Policy.Raw("apim-openai-policy.xml").Replace("{safety-inbound}", "").Replace("{safety-outbound}", "");
        Check("disabled", !baseline.Contains("send-request") && !baseline.Contains("contentsafety") && !baseline.Contains("rp-safety-input-state"), "disabled policy must not call Content Safety");
        Check("disabled", baseline.Contains("<set-header name=\"x-rp-gw-safety-output-checked\" exists-action=\"delete\" />") && baseline.Contains("<set-header name=\"x-rp-gw-safety\" exists-action=\"delete\" />"), "disabled policy must still strip caller-supplied safety evidence");
        var dis = Execute(S("disabled", Chat(("user", Secret)), null, null, s => s.InboundFragment = false));
        Check("disabled", dis.Status == 200 && dis.ModelCalled && dis.CsCalls.Count == 0 && dis.H("x-rp-gw-safety") == "", "disabled mode must pass through untouched");

        Console.WriteLine($"{checks} checks, {failures} failures");
        Console.WriteLine(failures == 0 ? "SELFTEST PASS" : "SELFTEST FAIL");
        return failures == 0 ? 0 : 1;
    }

}
