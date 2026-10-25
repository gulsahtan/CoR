using ChainOfRepair.Core.Experiments;
using System.Net;
using System.Text;
using System.Text.Json;
using ChainOfRepair.Core.Models;
using ChainOfRepair.Core.Parsing;
using ChainOfRepair.Core.Pipeline;
using ChainOfRepair.Core.Ranking;
using ChainOfRepair.Core.Reasoning;
using ChainOfRepair.Core.Validation;

if (args.Contains("--live-rca") || args.Contains("--live-stages"))
{
    var client = new OpenAiLLMClient(new LlmSettings());
    var service = new CoRReasoningService(client, new PromptTemplateService(Path.GetDirectoryName(FindRepoFile("prompts", "root_cause.md"))!));
    var request = new RepairRequest { SourceCode = File.ReadAllText(FindRepoFile("sample_inputs", "java-null-division", "source.java")),
        FileName = "Example.java", Language = SupportedLanguage.Java,
        FailingOutput = "java.lang.NullPointerException\n\tat Example.divide(Example.java:3)",
        BugDescription = "Division fails when input is null or zero." };
    var parsed = new JavaArtifactParser().Parse(request);
    var selected = new SuspiciousMethodScore { Method = parsed.CandidateMethods.Single() };
    try
    {
        var result = await service.GenerateRootCauseAsync(request, selected, parsed.FailingOutput, [selected]);
        Console.WriteLine(result.Response);
        using var doc = JsonDocument.Parse(result.Response);
        var cause = doc.RootElement.GetProperty("rootCause").GetString()!;
        Assert(cause.Contains("null", StringComparison.OrdinalIgnoreCase) && cause.Contains("unbox", StringComparison.OrdinalIgnoreCase), "Expected null Integer unboxing diagnosis.");
        Assert(doc.RootElement.GetProperty("localizedMethod").GetString() == "Example.divide", "Expected correct method.");
        Assert(client.Calls.Last().OutputTokens <= 512, "RCA output cap exceeded.");
        if (args.Contains("--live-stages"))
        {
            var patch = await service.GeneratePatchAsync(request, selected.Method, result.Response);
            Console.WriteLine(patch.Response);
            var applied = new PatchApplicationService().Apply(request, selected.Method, patch.Response);
            if (!applied.Applied)
            {
                var rejected = new ValidationResult { Passed = false, PatchApplication = applied,
                    Checks = [new("patch-application", false, string.Join("; ", applied.Messages))],
                    RefinementInstruction = "Correct the rejected unified diff: " + string.Join("; ", applied.Messages) };
                patch = await service.GenerateRefinementAsync(request, selected.Method, result.Response, patch.Response, applied, rejected, 1);
                Console.WriteLine(patch.Response);
                applied = new PatchApplicationService().Apply(request, selected.Method, patch.Response);
            }
            Assert(applied.Applied, "Generated diff must apply after at most one refinement: " + string.Join("; ", applied.Messages));
            var output = Path.Combine(Path.GetDirectoryName(FindRepoFile("README.md"))!, "logs", "live-stage-artifacts");
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "Example.java"), applied.PatchedCode);
            File.WriteAllText(Path.Combine(output, "patch.json"), patch.Response);
            File.WriteAllText(Path.Combine(output, "Smoke.java"), """
                public class Smoke {
                    public static void main(String[] args) {
                        Example example = new Example();
                        if (example.divide(2) != 5 || example.divide(-2) != -5) throw new AssertionError("Normal division changed");
                        try { example.divide(null); }
                        catch (NullPointerException ex) { throw new AssertionError("Null unboxing remains", ex); }
                        catch (IllegalArgumentException ex) { /* Explicit rejection is permitted by this smoke test. */ }
                        System.out.println("PASS normal division and null handling smoke check");
                    }
                }
                """);
            var compile = await CommandRunner.RunAsync(new() { Executable = "javac", Arguments = ["Example.java", "Smoke.java"] }, output, "Example.java");
            Assert(compile.Passed, "Generated Java must compile: " + compile.Error);
            var smoke = await CommandRunner.RunAsync(new() { Executable = "java", Arguments = ["-cp", ".", "Smoke"] }, output, "Example.java");
            Assert(smoke.Passed, "Generated Java smoke check: " + smoke.Error);
            Console.WriteLine(smoke.Output);
            var check = await service.GenerateSelfConsistencyAsync(request, selected.Method, result.Response, patch.Response, applied);
            Console.WriteLine(check.Response);
            using var checkDoc = JsonDocument.Parse(check.Response);
            Assert(new[] { "passed", "consistent", "aligned" }.All(k => checkDoc.RootElement.GetProperty(k).GetBoolean()), "CHECK rejected patch");
            var validation = new ValidationResult { Passed = true, PatchApplication = applied,
                Checks = [new("javac", compile.Passed, "Compiled generated Java"), new("smoke", smoke.Passed, smoke.Output)] };
            var explanation = await service.GenerateExplanationAsync(request, selected.Method, result.Response, patch.Response, check.Response, validation);
            Console.WriteLine(explanation.Response);
            Console.WriteLine("Live stage smoke test passed; full static-analysis/benchmark acceptance was not evaluated.");
        }
        return 0;
    }
    finally
    {
        var report = JsonSerializer.Serialize(new { client.Calls, service.Exchanges }, JsonLog.Options);
        var logPath = Path.Combine(Path.GetDirectoryName(FindRepoFile("README.md"))!, "logs");
        Directory.CreateDirectory(logPath);
        File.WriteAllText(Path.Combine(logPath, $"live-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json"), report);
        File.WriteAllText(Path.Combine(logPath, args.Contains("--live-stages") ? "live-stage-check.json" : "live-rca-check.json"), report);
        Console.WriteLine(JsonSerializer.Serialize(client.Calls, JsonLog.Options));
    }
}

var tests = new (string Name, Func<Task> Body)[]
{
    ("control-flow blocks are not callable methods", ControlBlocks),
    ("research metrics preserve missing observations and price cached tokens", ResearchMetrics),
    ("patch HTTP length recovery is capped at two calls", PatchLengthHttp),
    ("structured API contract and bounded invalid JSON recovery", StructuredApiContract),
    ("stage schemas and output validation", StageContracts),
    ("production DI registers OpenAiLLMClient", ProductionDiRegistration),
    ("missing OPENAI_API_KEY fails clearly", MissingApiKeyFailsClearly),
    ("different inputs are sent as different prompts", DifferentInputsProduceDifferentPrompts),
    ("pipeline does not return canned patches", PipelineDoesNotReturnCannedPatches),
    ("patch application changes actual source code", PatchApplicationChangesSource),
    ("validation runs on patched code", ValidationRunsOnPatchedCode),
    ("refinement sends validation errors back to LLM", RefinementReceivesValidationErrors),
    ("three-iteration refinement budget is enforced", RefinementBudget),
    ("run logs are created for each LLM stage", RunLogsCreatedForEachStage),
    ("parser extracts Java methods and stack trace artifacts", ParserAndStackTraceStillWork),
    ("Borda ranking and top-k ordering work", BordaCalculation),
    ("average Borda ties include zero evidence", AverageTies),
    ("CHECK rejects false and malformed judgments", CheckPolicy),
    ("search reaches later ranked methods", LaterMethods),
    ("rejected candidates never invoke EXPLAIN", NoExplanationOnFailure),
    ("differential analysis preserves old findings", DifferentialFindings),
    ("malformed hunks cannot modify source", MalformedHunks),
    ("API usage and call budgets are measured", ApiBudgets),
    ("unknown API usage stops further requests", ApiFailure),
    ("missing observations remain missing in reports", MissingReports),
    ("manifest rejects split overlap", ManifestOverlap),
    ("missing compiler fails closed", MissingCompiler),
    ("negative CHECK overrides deterministic pass", NegativeCheckGate),
    ("EXPLAIN failure retains accepted patch", ExplanationFailure),
    ("scope tracks inserted lines after a replacement", ScopeInsertion),
    ("incomplete repeated experiments are missing", IncompleteRepeats),
    ("comments and strings do not create methods", LexicalMasking),
    ("braces inside strings do not alter boundaries", StringBraces),
    ("truncated output retries once and retains both responses", TruncationRecovery),
    ("repeated truncation remains visible and cannot apply a patch", TruncationExhausted),
    ("truncation retry respects API call budget", TruncationBudget)
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        await test.Body();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failures.Add($"{test.Name}: {ex.Message}");
        Console.WriteLine($"FAIL {test.Name}: {ex.Message}");
    }
}

if (failures.Count > 0)
{
    Console.Error.WriteLine(string.Join(Environment.NewLine, failures));
    return 1;
}

Console.WriteLine($"All {tests.Length} tests passed.");
return 0;

static Task ControlBlocks()
{
    var source = "class A {\n public static int f(int n) {\n while(n>0) {\n if(n==2) { n--; }\n for(int i=0;i<1;i++) { n--; }\n n--;\n }\n return n;\n }\n}";
    var parsed = new JavaArtifactParser().Parse(new() { SourceCode = source, FileName = "A.java" });
    Assert(parsed.CandidateMethods.Count == 1 && parsed.CandidateMethods[0].Name == "f", "Control headers must not overlap the real method");
    var synchronized = new JavaArtifactParser().Parse(new() { SourceCode = "class A {\n synchronized int f() { return 1; }\n}", FileName = "A.java" });
    Assert(synchronized.CandidateMethods.Count == 1, "Synchronized method modifiers remain callable");
    return Task.CompletedTask;
}

static async Task ResearchMetrics()
{
    Assert(IsolatedProject.SafePath(Path.GetTempPath(), "Example.java").EndsWith("Example.java"), "Trailing root separator must not reject safe relative source paths");
    try { IsolatedProject.SafePath(Path.GetTempPath(), "../escape.java"); throw new Exception("Traversal accepted"); }
    catch (InvalidDataException) { }
    var pricing = new ApiPricing("snapshot", 2.5m, 1.25m, 10m, "official", "2026-10-25");
    var call = new ModelCall(1, 1000, 100, 1, "snapshot", "Stop", null) { CachedInputTokens = 200 };
    Assert(ResearchReports.Cost(call, pricing) == .00325m, "Cached tokens must not be charged twice");
    Assert(ResearchReports.Cost(call with { CachedInputTokens = null }, pricing) is null, "Missing cache usage is not zero");
    Assert(ResearchReports.Cost(call with { ResponseModel = "different" }, pricing) is null, "Do not guess prices for another snapshot");
    Assert(ResearchReports.FailedTests(new(1, "{\"failed\":3}", 1, "tests failed")) == 3, "Keep failing test counts");
    Assert(ResearchReports.FailedTests(new(0, "OK", 1, null)) is null, "Unknown test counts must remain missing");
    Assert(ResearchReports.TestPass(new(2, "", 1, "tool missing")) is null, "Infrastructure error is not test failure");
    var output = Path.Combine(Path.GetTempPath(), "cor-research-test-" + Guid.NewGuid().ToString("n")); Directory.CreateDirectory(output);
    await ResearchReports.WriteAsync([new() { RunId = "missing", Benchmark = "fixture", ExpectedRepeats = 2, Result = new() { FinalStatus = "setup-error" } }], output, default);
    var summary = File.ReadAllText(Path.Combine(output, "summary_results.csv"));
    Assert(summary.Contains("\"root_cause_accuracy\",\"run_observation\",\"2\",\"0\",\"2\",\"0\",\"0\",\"\""), "No RCA labels must produce an undefined rate and explicit missing count");
}

static async Task PatchLengthHttp()
{
    var previous = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
    Environment.SetEnvironmentVariable("OPENAI_API_KEY", "offline-test-not-a-secret");
    try
    {
        var handler = new ApiHandler(false, "length");
        var client = new OpenAiLLMClient(new() { MaxTokens = 4096 }, new HttpClient(handler));
        var service = new CoRReasoningService(client, new PromptTemplateService(Path.GetDirectoryName(FindRepoFile("prompts", "root_cause.md"))!));
        var request = SimpleRequest(1, 2);
        var method = new JavaArtifactParser().Parse(request).CandidateMethods.Single();
        await AssertThrowsAsync<LlmStageException>(() => service.GeneratePatchAsync(request, method, TestPayloads.RootCauseJson()), "output limit");
        Assert(handler.Count == 2 && client.Calls[0].MaxOutputTokens == 2048 && client.Calls[1].MaxOutputTokens == 4096, "Patch growth must be bounded to one retry");
        Assert(service.Exchanges.All(e => !e.IsComplete) && client.Calls.All(c => c.FinishReason == "Length"), "Neither partial patch may be accepted");
        using var body = JsonDocument.Parse(handler.LastBody);
        Assert(body.RootElement.GetProperty("max_completion_tokens").GetInt32() == 4096, "SDK must dispatch larger retry cap");
    }
    finally { Environment.SetEnvironmentVariable("OPENAI_API_KEY", previous); }
}

static async Task StructuredApiContract()
{
    var previous = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
    Environment.SetEnvironmentVariable("OPENAI_API_KEY", "offline-test-not-a-secret");
    try
    {
        var handler = new ApiHandler(false);
        var client = new OpenAiLLMClient(new(), new HttpClient(handler));
        var service = new CoRReasoningService(client, new PromptTemplateService(Path.GetDirectoryName(FindRepoFile("prompts", "root_cause.md"))!));
        var request = SimpleRequest(1, 2);
        var parsed = new JavaArtifactParser().Parse(request);
        var method = new SuspiciousMethodScore { Method = parsed.CandidateMethods.Single() };
        await AssertThrowsAsync<LlmStageException>(() => service.GenerateRootCauseAsync(request, method, parsed.FailingOutput, [method]), "JSON");
        Assert(handler.Count == 2 && service.Exchanges.Count == 2, "Invalid JSON must retry once and retain rejected responses");
        Assert(client.Calls.All(c => c.ErrorKind == "invalid-json") && client.Calls[1].RetryCount == 1, "Measured calls need error type and retry index");
        using var body = JsonDocument.Parse(handler.LastBody);
        var root = body.RootElement;
        Assert(root.GetProperty("response_format").GetProperty("type").GetString() == "json_schema", "SDK must send schema format");
        Assert(root.GetProperty("response_format").GetProperty("json_schema").GetProperty("strict").GetBoolean(), "Strict schema required");
        Assert(root.GetProperty("max_completion_tokens").GetInt32() == 512, "SDK must send stage output cap");
        Assert(client.Calls.All(c => c.TotalTokens == 15), "Rejected output still consumes measured tokens");
    }
    finally { Environment.SetEnvironmentVariable("OPENAI_API_KEY", previous); }
}

static Task StageContracts()
{
    Assert(StageOutputPolicy.Limit("root-cause", new LlmSettings(), true) == 512, "RCA must not grow on retry");
    Assert(StageOutputPolicy.Limit("patch-synthesis", new LlmSettings()) == 2048, "Initial patch limit");
    Assert(StageOutputPolicy.Limit("patch-synthesis", new LlmSettings { MaxTokens = 4096 }, true) == 4096, "Only length retry may grow patch");
    Assert(StageOutputPolicy.Limit("patch-synthesis", new LlmSettings(), true) == 2048, "Default paper ceiling applies to retries");
    Assert(StageOutputPolicy.Limit("patch-synthesis", new LlmSettings { MaxTokens = 2048 }, true) == 2048, "Honor explicit ceiling");
    try { _ = new OpenAiLLMClient(new() { MaxTokens = 512 }); throw new Exception("Shared cap silently reduces patch limits"); }
    catch (LlmConfigurationException) { }
    Assert(StageOutputPolicy.Limit("root-cause", new LlmSettings()) == 512, "RCA limit");
    Assert(StageOutputPolicy.Limit("patch-synthesis", new LlmSettings()) == 2048, "Patch limit");
    StageOutputPolicy.Validate("root-cause", TestPayloads.RootCauseJson());
    foreach (var (value, kind) in new[] { ("{", "invalid-json"), ("{}", "invalid-schema"),
        (TestPayloads.RootCauseJson().Replace("target returns the wrong constant for the supplied assertion.", new string('x', 601)), "meaningless-output") })
    {
        try { StageOutputPolicy.Validate("root-cause", value); throw new Exception("Accepted invalid output"); }
        catch (InvalidModelOutputException ex) { Assert(ex.Kind == kind, "Incorrect error classification"); }
    }
    using var schema = JsonDocument.Parse(StageOutputPolicy.Schema("root-cause")!);
    Assert(!schema.RootElement.GetProperty("additionalProperties").GetBoolean(), "Strict schema");
    return Task.CompletedTask;
}

static Task ProductionDiRegistration()
{
    var programPath = FindRepoFile("src", "ChainOfRepair.Web", "Program.cs");
    var program = File.ReadAllText(programPath);
    Assert(program.Contains("AddScoped<ILLMClient, OpenAiLLMClient>", StringComparison.Ordinal), "expected production DI to register OpenAiLLMClient");
    Assert(program.Contains("UseMockClient", StringComparison.Ordinal), "expected UseMockClient gate");
    return Task.CompletedTask;
}

static async Task MissingApiKeyFailsClearly()
{
    var previous = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
    Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
    try
    {
        var client = new OpenAiLLMClient(new LlmSettings());
        await AssertThrowsAsync<LlmConfigurationException>(
            () => client.CompleteAsync("system", "user"),
            "OPENAI_API_KEY");
    }
    finally
    {
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", previous);
    }
}

static async Task DifferentInputsProduceDifferentPrompts()
{
    var temp = CreatePromptDirectory();
    var client = new RecordingLLMClient();
    var reasoning = new CoRReasoningService(client, new PromptTemplateService(temp));

    var methodA = new SuspiciousMethodScore { Method = new CandidateMethod { Name = "alpha", Context = "Example", StartLine = 1, EndLine = 1, Source = "int alpha(){return 1;}" } };
    var methodB = new SuspiciousMethodScore { Method = new CandidateMethod { Name = "beta", Context = "Example", StartLine = 1, EndLine = 1, Source = "int beta(){return 2;}" } };

    await reasoning.GenerateRootCauseAsync(new RepairRequest { SourceCode = "A", FailingOutput = "fail alpha", Language = SupportedLanguage.Java }, methodA, new FailingOutputAnalysis(), [methodA]);
    await reasoning.GenerateRootCauseAsync(new RepairRequest { SourceCode = "B", FailingOutput = "fail beta", Language = SupportedLanguage.Java }, methodB, new FailingOutputAnalysis(), [methodB]);

    Assert(client.UserPrompts.Count == 2, "expected two prompts");
    Assert(client.UserPrompts[0] != client.UserPrompts[1], "expected prompts to differ for different inputs");
    Assert(client.UserPrompts[0].Contains("alpha", StringComparison.Ordinal), "first prompt should contain first method");
    Assert(client.UserPrompts[1].Contains("beta", StringComparison.Ordinal), "second prompt should contain second method");
}

static async Task PipelineDoesNotReturnCannedPatches()
{
    var temp = Path.Combine(Path.GetTempPath(), "cor-tests-" + Guid.NewGuid().ToString("n"));
    var pipeline = CreatePipeline(new RecordingLLMClient(), temp);
    var result = await pipeline.RunAsync(SimpleRequest(returnValue: 1, expectedValue: 2));

    Assert(!result.Reasoning.PatchJson.Contains("Minimal method-scoped patch candidate", StringComparison.OrdinalIgnoreCase), "should not return old canned patch text");
    Assert(!result.Reasoning.PatchJson.Contains("placeholder", StringComparison.OrdinalIgnoreCase), "should not return placeholder patch");
    Assert(result.Reasoning.PatchJson.Contains("\"unifiedDiff\"", StringComparison.Ordinal), "expected patch JSON with unifiedDiff");
}

static Task PatchApplicationChangesSource()
{
    var service = new PatchApplicationService();
    var result = service.Apply(
        SimpleRequest(returnValue: 1, expectedValue: 2),
        new CandidateMethod { Name = "target", Context = "Example", StartLine = 2, EndLine = 2 },
        TestPayloads.PatchJson(oldValue: 1, newValue: 2));

    Assert(result.Applied, "expected patch to apply");
    Assert(result.OriginalCode.Contains("return 1", StringComparison.Ordinal), "expected original code retained");
    Assert(result.PatchedCode.Contains("return 2", StringComparison.Ordinal), "expected patched code changed");
    return Task.CompletedTask;
}

static async Task ValidationRunsOnPatchedCode()
{
    var request = SimpleRequest(returnValue: 1, expectedValue: 2);
    var method = new CandidateMethod { Name = "target", Context = "Example", StartLine = 2, EndLine = 2 };
    var application = new PatchApplicationService().Apply(request, method, TestPayloads.PatchJson(oldValue: 1, newValue: 2));
    var validation = await new ValidationService().ValidateAsync(request, method, TestPayloads.RootCauseJson(), TestPayloads.PatchJson(oldValue: 1, newValue: 2), application);

    Assert(validation.PatchApplication.PatchedCode.Contains("return 2", StringComparison.Ordinal), "validation should carry patched code");
    Assert(!validation.Passed, "unconfigured static analysis must not pass");
}

static async Task RefinementReceivesValidationErrors()
{
    var client = new AlwaysBadLLMClient();
    var pipeline = CreatePipeline(client, Path.Combine(Path.GetTempPath(), "cor-tests-" + Guid.NewGuid().ToString("n")));
    await pipeline.RunAsync(SimpleRequest(returnValue: 1, expectedValue: 2));

    Assert(client.RefinementPrompts.Count > 0, "expected at least one refinement prompt");
    Assert(client.RefinementPrompts[0].Contains("Validation failed", StringComparison.Ordinal), "expected validation summary in refinement prompt");
    Assert(client.RefinementPrompts[0].Contains("could not be applied", StringComparison.Ordinal), "expected validation error detail in refinement prompt");
}

static async Task RefinementBudget()
{
    var temp = Path.Combine(Path.GetTempPath(), "cor-tests-" + Guid.NewGuid().ToString("n"));
    var result = await CreatePipeline(new AlwaysBadLLMClient(), temp).RunAsync(SimpleRequest(returnValue: 1, expectedValue: 2));

    Assert(result.RefinementIterations.Count == 3, "expected exactly three refinements");
    Assert(result.FinalStatus == "unrepaired", "expected unrepaired after failed budget");
}

static async Task RunLogsCreatedForEachStage()
{
    var temp = Path.Combine(Path.GetTempPath(), "cor-tests-" + Guid.NewGuid().ToString("n"));
    var result = await CreatePipeline(new RecordingLLMClient(), temp).RunAsync(SimpleRequest(returnValue: 1, expectedValue: 2));

    var runDir = result.LogDirectory;
    foreach (var file in new[]
             {
                 "input.json",
                 "ranked_methods.json",
                 "root_cause_prompt.txt",
                 "root_cause_response.json",
                 "patch_synthesis_prompt.txt",
                 "patch_synthesis_response.json",
                 "self_consistency_prompt.txt",
                 "self_consistency_response.json",
                 "patched_code.txt",
                 "validation_result.json",
                 "explanation_prompt.txt",
                 "explanation_response.txt",
                 "final_result.json"
             })
    {
        Assert(File.Exists(Path.Combine(runDir, file)), $"expected log file {file}");
    }
}

static Task ParserAndStackTraceStillWork()
{
    var parser = new JavaArtifactParser();
    var result = parser.Parse(SimpleRequest(returnValue: 1, expectedValue: 2));
    Assert(result.CandidateMethods.Count == 1, "expected one method");
    Assert(result.CandidateMethods[0].Name == "target", "expected method target");
    Assert(result.FailingOutput.StackTraceFrames.Count == 1, "expected stack frame");
    Assert(result.FailingOutput.MethodNames.Contains("target"), "expected target method from stack");
    return Task.CompletedTask;
}

static Task BordaCalculation()
{
    var request = new RepairRequest
    {
        SourceCode = """
            class Example {
              int safe(int x) { return x; }
              int risky(Integer x) { return 10 / x; }
            }
            """,
        FailingOutput = "at Example.risky(Example.java:3)",
        BugDescription = "risky null division",
        Language = SupportedLanguage.Java,
        TopK = 3
    };
    var preprocessing = new JavaArtifactParser().Parse(request);
    var ranking = new WeightedBordaRanker().Rank(request, preprocessing);
    Assert(ranking.RankedMethods.Count == 2, "expected two ranked methods");
    Assert(ranking.RankedMethods[0].Method.Name == "risky", "expected risky first");
    return Task.CompletedTask;
}

static Task AverageTies()
{
    var scores = WeightedBordaRanker.Borda([4, 4, 0, 0]);
    Assert(Math.Abs(scores[0] - 5.0 / 6) < 1e-9 && scores[0] == scores[1], "top ties must average occupied ranks");
    Assert(Math.Abs(scores[2] - 1.0 / 6) < 1e-9 && scores[2] == scores[3], "zero ties also receive average rank");
    Assert(WeightedBordaRanker.Borda([0])[0] == 1, "singleton must receive one");
    return Task.CompletedTask;
}
static Task CheckPolicy()
{
    Assert(!RepairPipelineService.CheckPasses("{\"passed\":true}"), "missing alignment must fail");
    Assert(!RepairPipelineService.CheckPasses("not json"), "malformed CHECK must fail");
    Assert(!RepairPipelineService.CheckPasses("{\"passed\":true,\"consistent\":true,\"aligned\":false}"), "negative alignment must fail");
    return Task.CompletedTask;
}
static async Task LaterMethods()
{
    var request = SimpleRequest(1, 2) with { SourceCode = "class Example {\n int alpha(){ return 1; }\n int beta(){ return 1; }\n}", FailingOutput = "", BugDescription = null, MaxRefinements = 1 };
    var client = new AlwaysBadLLMClient();
    var result = await CreatePipeline(client, Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())).RunAsync(request);
    Assert(result.Candidates.Count == 4, "two candidates per method expected");
    Assert(result.Candidates.Select(c => c.MethodId).Distinct().Count() == 2, "search must visit both methods");
}
static async Task NoExplanationOnFailure()
{
    var client = new RecordingLLMClient();
    var result = await CreatePipeline(client, Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())).RunAsync(SimpleRequest(8, 2));
    Assert(!result.Accepted && result.Reasoning.Exchanges.All(e => e.Stage != "explanation"), "rejected patch must not be explained as accepted");
}
static Task DifferentialFindings()
{
    var finding = new StaticFinding("tool", "X", "file", "method", "null-safety", 2);
    Assert(ValidationService.DifferentialFindings([finding], [finding], 2).Length == 0, "old finding should not reject");
    Assert(ValidationService.DifferentialFindings([finding], [finding with { Severity = 3 }], 2).Length == 1, "worsened finding should reject");
    Assert(ValidationService.DifferentialFindings([finding], [finding, finding], 2).Length == 1, "new duplicate finding should reject");
    return Task.CompletedTask;
}
static Task MalformedHunks()
{
    var bad = TestPayloads.PatchJson(1, 2).Replace("@@ -2,1 +2,1 @@", "@@ -2,8 +2,8 @@");
    var result = new PatchApplicationService().Apply(SimpleRequest(1, 2), new() { Name = "target", StartLine = 2, EndLine = 2 }, bad);
    Assert(!result.Applied && result.PatchedCode == result.OriginalCode, "line count mismatch must fail");
    return Task.CompletedTask;
}
static async Task ApiBudgets()
{
    var previous = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
    Environment.SetEnvironmentVariable("OPENAI_API_KEY", "offline-test-not-a-secret");
    try
    {
        var handler = new ApiHandler(false);
        var client = new OpenAiLLMClient(new() { MaxCalls = 1 }, new HttpClient(handler));
        await client.CompleteAsync("system", "input");
        Assert(client.Calls.Single().InputTokens == 10 && client.Calls.Single().OutputTokens == 5, "server usage must be recorded");
        await AssertThrowsAsync<InferenceBudgetException>(() => client.CompleteAsync("system", "input"), "budget");
        Assert(handler.Count == 1, "budget must prevent a second HTTP call");
        var small = new OpenAiLLMClient(new() { TotalTokenBudget = 100 }, new HttpClient(handler));
        await AssertThrowsAsync<InferenceBudgetException>(() => small.CompleteAsync("system", "input"), "budget");
        Assert(handler.Count == 1, "token reservation must reject before dispatch");
        client.BeginRun(); Assert(client.Calls.Count == 0, "budgets reset per defect");
    }
    finally { Environment.SetEnvironmentVariable("OPENAI_API_KEY", previous); }
}
static async Task ApiFailure()
{
    var previous = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
    Environment.SetEnvironmentVariable("OPENAI_API_KEY", "offline-test-not-a-secret");
    try
    {
        var handler = new ApiHandler(true); var client = new OpenAiLLMClient(new(), new HttpClient(handler));
        await AssertThrowsAsync<LlmCallException>(() => client.CompleteAsync("s", "u"), "HTTP");
        Assert(client.Calls.Count == 1 && client.Calls[0].InputTokens is null, "unknown usage must remain null");
        await AssertThrowsAsync<InferenceBudgetException>(() => client.CompleteAsync("s", "u"), "budget");
        Assert(handler.Count == 1, "SDK must not retry");
    }
    finally { Environment.SetEnvironmentVariable("OPENAI_API_KEY", previous); }
}
static async Task MissingReports()
{
    var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    var record = new ExperimentRecord { RunId = "test-record", Benchmark = "QuixBugs", BugId = "synthetic-test-only", Configuration = "test", Split = "evaluation", Repeat = 1,
        Result = new() { FrameworkPlausible = null, FinalStatus = "setup-error" } };
    await ExperimentReports.WriteAsync([record], dir);
    var table = File.ReadAllLines(Path.Combine(dir, "tables.csv")).Single(l => l.Contains("\"QuixBugs\",\"correct_repair\""));
    Assert(table.Contains("\"1\",\"0\",\"1\",\"\",\"\",\"\""), "missing rates must stay blank with missing count");
    Assert(JsonSerializer.Serialize(record, JsonLog.Options).Contains("\"correctRepair\": null"), "JSON null must remain visible");
}
static Task ManifestOverlap()
{
    var item = new ExperimentCase { Benchmark = "QuixBugs", BugId = "x", Revision = "test", SourceFiles = ["a.py"] };
    try { ExperimentRunner.ValidateManifest(new() { Cases = [item, item with { Split = "calibration" }] }); }
    catch (InvalidDataException) { return Task.CompletedTask; }
    throw new Exception("split overlap was accepted");
}
static async Task MissingCompiler()
{
    var request = SimpleRequest(1, 2); var method = new JavaArtifactParser().Parse(request).CandidateMethods.Single();
    var application = new PatchApplicationService().Apply(request, method, TestPayloads.PatchJson(1, 2));
    var validator = new ValidationService(new() { Build = new() { Executable = "cor-nonexistent-compiler" } });
    var result = await validator.ValidateAsync(request, method, "{}", "{}", application);
    Assert(!result.Passed && result.Checks.Any(c => c.Rule == "compilation" && !c.Passed), "missing compiler must fail closed");
}

static async Task NegativeCheckGate()
{
    var result = await CreatePipeline(new InterceptClient("causally consistent", "{\"passed\":false,\"consistent\":false,\"aligned\":false}", false),
        Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())).RunAsync(SimpleRequest(1, 2) with { MaxRefinements = 0 });
    Assert(!result.Accepted && result.FinalValidation.Checks.Any(c => c.Rule == "causal-alignment" && !c.Passed), "CHECK must veto acceptance");
}
static async Task ExplanationFailure()
{
    var result = await CreatePipeline(new InterceptClient("explain automated", "", true), Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())).RunAsync(SimpleRequest(1, 2));
    Assert(result.Accepted && result.FinalStatus == "framework-plausible", "explanation error must preserve accepted patch");
    Assert(result.ErrorMessage.Contains("explanation unavailable"), "explanation failure must remain visible");
}
static Task ScopeInsertion()
{
    var request = SimpleRequest(1, 2);
    var json = JsonSerializer.Serialize(new { unifiedDiff = "--- a/input\n+++ b/input\n@@ -2,2 +2,3 @@\n- int target(){ return 1; }\n+ int target(){ return 2; }\n }\n+int unrelated;", targetMethod = "target" });
    var result = new PatchApplicationService().Apply(request, new() { Name = "target", StartLine = 2, EndLine = 2 }, json);
    Assert(result.Applied && result.ChangedRanges.Any(r => r.OldEndLine > 2), "outside insertion must be represented in scope ranges");
    return Task.CompletedTask;
}
static async Task IncompleteRepeats()
{
    var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    await ExperimentReports.WriteAsync([new() { RunId = "test-repeat", Benchmark = "QuixBugs", BugId = "fixture", Split = "evaluation", Configuration = "test", Repeat = 1, ExpectedRepeats = 2, CorrectRepair = true }], dir);
    var row = File.ReadAllLines(Path.Combine(dir, "tables.csv")).Single(l => l.Contains("\"QuixBugs\",\"correct_repair\""));
    Assert(row.Contains("\"1\",\"0\",\"1\",\"\",\"\",\"\""), "absent repeat must not inflate observed rate");
}

static Task LexicalMasking()
{
    var source = "class Example {\n // int fake(){return 0;}\n int real(){ String text = \"int invented(){return 1;}\"; return 1; }\n}";
    var parsed = new JavaArtifactParser().Parse(new() { SourceCode = source, FileName = "Example.java" });
    Assert(parsed.CandidateMethods.Count == 1 && parsed.CandidateMethods[0].Name == "real", "comment/string declarations must not be indexed");
    Assert(!RepairPipelineService.CheckPasses("[]"), "non-object CHECK must reject");
    return Task.CompletedTask;
}
static Task StringBraces()
{
    var source = "class Example {\n int first(){\n String s = \"}\";\n return 1;\n }\n int second(){return 2;}\n}";
    var parsed = new JavaArtifactParser().Parse(new() { SourceCode = source, FileName = "Example.java" });
    Assert(parsed.CandidateMethods[0].EndLine == 5 && parsed.CandidateMethods[1].StartLine == 6, "string braces must not end a method");
    return Task.CompletedTask;
}

static async Task TruncationRecovery()
{
    var client = new TruncatingClient(1);
    var result = await CreatePipeline(client, Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())).RunAsync(SimpleRequest(1, 2));
    Assert(result.Accepted, "complete regeneration should proceed through normal validation");
    var attempts = result.Reasoning.Exchanges.Where(e => e.Stage == "patch-synthesis").ToArray();
    Assert(attempts.Length == 2 && !attempts[0].IsComplete && attempts[1].IsComplete && attempts[1].Attempt == 2, "both attempts must be retained");
    Assert(attempts[0].Response == "{truncated offline fixture", "partial response must not be discarded");
    Assert(attempts[0].MaxTokens == 2048 && attempts[1].MaxTokens == 4096, "Patch retry gets a bounded larger cap");
    Assert(attempts[1].UserPrompt.StartsWith("INPUT ARTIFACT JSON:"), "Retry must omit repeated template instructions");
    Assert(attempts[1].UserPrompt.Contains("RECOVERY (output-limit)"), "retry must explicitly request compact regeneration");
    Assert(result.Candidates.Count == 1, "incomplete output must never reach candidate application");
}
static async Task TruncationExhausted()
{
    var pipeline = CreatePipeline(new TruncatingClient(int.MaxValue), Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
    for (var run = 0; run < 2; run++)
    {
        var result = await pipeline.RunAsync(SimpleRequest(1, 2));
        Assert(result.FinalStatus == "output-truncated" && !result.Accepted && result.FrameworkPlausible is null, "exhaustion must be an explicit incomplete result");
        Assert(result.Reasoning.Exchanges.Count == 3, "journal must reset per run and include two incomplete attempts");
        Assert(result.Reasoning.PatchJson == "" && result.Candidates.Count == 0, "truncated output must not become a patch");
        Assert(File.ReadAllText(Path.Combine(result.LogDirectory, "patch_synthesis_attempt_2_incomplete_response.txt")) == "{truncated offline fixture", "partial response must be saved to disk");
        Assert(!string.IsNullOrEmpty(result.Reasoning.RootCause), "completed diagnosis must remain available");
    }
}
static async Task TruncationBudget()
{
    var previous = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
    Environment.SetEnvironmentVariable("OPENAI_API_KEY", "offline-test-not-a-secret");
    try
    {
        var handler = new ApiHandler(false, "length");
        var client = new OpenAiLLMClient(new() { MaxCalls = 1 }, new HttpClient(handler));
        var reasoning = new CoRReasoningService(client, new(CreatePromptDirectory()));
        await AssertThrowsAsync<LlmStageException>(() => reasoning.GeneratePatchAsync(SimpleRequest(1, 2), new() { StartLine = 2, EndLine = 2 }, "{}"), "budget");
        Assert(handler.Count == 1 && client.Calls.Count == 1, "recovery must not exceed the call budget");
        Assert(reasoning.Exchanges.Single().Response == "ok" && !reasoning.Exchanges.Single().IsComplete, "SDK must preserve Length content");
        Assert(client.Calls.Single().InputTokens == 10 && client.Calls.Single().FinishReason == "Length", "truncated usage is measured and retained");
    }
    finally { Environment.SetEnvironmentVariable("OPENAI_API_KEY", previous); }
}

static RepairPipelineService CreatePipeline(ILLMClient client, string logRoot)
{
    return new RepairPipelineService(
        new CoRReasoningService(client, new PromptTemplateService(CreatePromptDirectory())),
        new PatchApplicationService(),
        new TestValidationService(),
        new WeightedBordaRanker(),
        client,
        logRoot);
}

static RepairRequest SimpleRequest(int returnValue, int expectedValue)
{
    return new RepairRequest
    {
        SourceCode = $$"""
            class Example {
             int target(){ return {{returnValue}}; }
            }
            """,
        FailingOutput = $"java.lang.AssertionError: expected {expectedValue}\n\tat Example.target(Example.java:2)",
        BugDescription = $"target should return {expectedValue}",
        Language = SupportedLanguage.Java,
        TopK = 3,
        FileName = "Example.java"
    };
}

static string CreatePromptDirectory()
{
    var dir = Path.Combine(Path.GetTempPath(), "cor-prompts-" + Guid.NewGuid().ToString("n"));
    Directory.CreateDirectory(dir);
    File.WriteAllText(Path.Combine(dir, "root_cause.md"), "root cause template");
    File.WriteAllText(Path.Combine(dir, "patch_synthesis.md"), "patch synthesis template");
    File.WriteAllText(Path.Combine(dir, "self_consistency.md"), "self consistency template");
    File.WriteAllText(Path.Combine(dir, "refinement.md"), "refinement template");
    File.WriteAllText(Path.Combine(dir, "explanation.md"), "explanation template");
    File.WriteAllText(Path.Combine(dir, "direct_patch.md"), "direct patch template");
    return dir;
}

static string FindRepoFile(params string[] parts)
{
    var current = Directory.GetCurrentDirectory();
    while (!string.IsNullOrWhiteSpace(current))
    {
        var candidate = Path.Combine(new[] { current }.Concat(parts).ToArray());
        if (File.Exists(candidate))
        {
            return candidate;
        }

        current = Directory.GetParent(current)?.FullName ?? "";
    }

    throw new FileNotFoundException("Could not find repo file: " + Path.Combine(parts));
}

static async Task AssertThrowsAsync<T>(Func<Task> body, string expectedMessagePart) where T : Exception
{
    try
    {
        await body();
    }
    catch (T ex)
    {
        Assert(ex.Message.Contains(expectedMessagePart, StringComparison.Ordinal), $"expected message to contain {expectedMessagePart}");
        return;
    }

    throw new InvalidOperationException($"Expected exception {typeof(T).Name}.");
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

internal static class TestPayloads
{
    public static string RootCauseJson() =>
        """
        {"faultCategory":"incorrect return value","rootCause":"target returns the wrong constant for the supplied assertion.","evidence":["failing output references Example.target"],"localizedMethod":"Example.target","confidence":0.9}
        """;

    public static string PatchJson(int oldValue, int newValue) =>
        $$"""
        {"patchType":"unified-diff","targetMethod":"target","changedLinesRationale":"Replace the incorrect returned constant.","unifiedDiff":"--- a/input\n+++ b/input\n@@ -2,1 +2,1 @@\n- int target(){ return {{oldValue}}; }\n+ int target(){ return {{newValue}}; }","expectedEffect":"target returns the expected value."}
        """;

    public static string BadPatchJson() =>
        """
        {"patchType":"unified-diff","targetMethod":"target","changedLinesRationale":"Bad line.","unifiedDiff":"--- a/input\n+++ b/input\n@@ -10,1 +10,1 @@\n- int other(){ return 1; }\n+ int other(){ return 2; }","expectedEffect":"none"}
        """;
}

internal sealed class RecordingLLMClient : ILLMClient
{
    public LlmSettings Settings { get; } = new();
    public string ClientName => "RecordingLLMClient";
    public bool IsMock => false;
    public List<string> UserPrompts { get; } = [];

    public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
    {
        UserPrompts.Add(userPrompt);
        if (systemPrompt.Contains("root-cause", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(TestPayloads.RootCauseJson());
        }

        if (systemPrompt.Contains("minimal program repair patches", StringComparison.OrdinalIgnoreCase) ||
            systemPrompt.Contains("refine failed repair patches", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(TestPayloads.PatchJson(oldValue: 1, newValue: 2));
        }

        if (systemPrompt.Contains("causally consistent", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult("""{"passed":true,"consistent":true,"aligned":true,"concerns":[],"causalAlignment":"aligned","scopeAssessment":"method-scoped"}""");
        }

        return Task.FromResult("The target method returned the wrong value; the patch changes the returned constant and validation checked the patched source.");
    }
}

internal sealed class AlwaysBadLLMClient : ILLMClient
{
    public LlmSettings Settings { get; } = new();
    public string ClientName => "AlwaysBadLLMClient";
    public bool IsMock => false;
    public List<string> RefinementPrompts { get; } = [];

    public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
    {
        if (systemPrompt.Contains("root-cause", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(TestPayloads.RootCauseJson());
        }

        if (systemPrompt.Contains("refine failed repair patches", StringComparison.OrdinalIgnoreCase))
        {
            RefinementPrompts.Add(userPrompt);
            return Task.FromResult(TestPayloads.BadPatchJson());
        }

        if (systemPrompt.Contains("minimal program repair patches", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(TestPayloads.BadPatchJson());
        }

        if (systemPrompt.Contains("causally consistent", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult("""{"passed":false,"concerns":["bad patch"],"causalAlignment":"not aligned","scopeAssessment":"invalid"}""");
        }

        return Task.FromResult("The patch failed validation after the refinement budget.");
    }
}

// Deterministic collaborators exist only in the test executable, never production experiment runs.
internal sealed class TestValidationService : IValidationService
{
    public Task<ValidationResult> ValidateAsync(RepairRequest request, CandidateMethod method, string diagnosis,
        string patch, PatchApplicationResult application, CancellationToken cancellationToken = default) => Task.FromResult(
        new ValidationResult { Passed = application.Applied, PatchApplication = application,
            Checks = [new("test-only", application.Applied, "offline test collaborator")],
            RefinementInstruction = "Validation failed: unified diff could not be applied." });
}
internal sealed class ApiHandler(bool fail, string finishReason = "stop") : HttpMessageHandler
{
    public int Count { get; private set; }
    public string LastBody { get; private set; } = "";
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Count++;
        LastBody = await request.Content!.ReadAsStringAsync(cancellationToken);
        return new HttpResponseMessage(fail ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK)
        { Content = new StringContent(fail ? "{\"error\":{\"message\":\"offline test\",\"type\":\"rate_limit_error\"}}" :
            "{\"id\":\"test\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"gpt-4o\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":\"" + finishReason + "\"}],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":5,\"total_tokens\":15}}", Encoding.UTF8, "application/json") };
    }
}

internal sealed class InterceptClient(string stage, string response, bool fail) : ILLMClient
{
    private readonly RecordingLLMClient inner = new();
    public LlmSettings Settings => inner.Settings;
    public string ClientName => "offline-intercept";
    public bool IsMock => true;
    public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
    {
        if (!systemPrompt.Contains(stage, StringComparison.OrdinalIgnoreCase)) return inner.CompleteAsync(systemPrompt, userPrompt, cancellationToken);
        if (fail) throw new LlmCallException("Offline injected failure");
        return Task.FromResult(response);
    }
}

internal sealed class TruncatingClient(int failures) : ILLMClient
{
    private readonly RecordingLLMClient inner = new();
    private int patchCalls;
    public LlmSettings Settings => inner.Settings with { MaxTokens = 4096 };
    public string ClientName => "offline-truncation";
    public bool IsMock => true;
    public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
    {
        if (systemPrompt.Contains("minimal program repair patches", StringComparison.OrdinalIgnoreCase) && patchCalls++ < failures)
            throw new LlmOutputTruncatedException("{truncated offline fixture", 2048);
        return inner.CompleteAsync(systemPrompt, userPrompt, cancellationToken);
    }
}
