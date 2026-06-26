using System.Text.Json;
using ChainOfRepair.Core.Models;
using ChainOfRepair.Core.Parsing;
using ChainOfRepair.Core.Pipeline;
using ChainOfRepair.Core.Ranking;
using ChainOfRepair.Core.Reasoning;
using ChainOfRepair.Core.Validation;

var tests = new (string Name, Func<Task> Body)[]
{
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
    ("Borda ranking and top-k ordering work", BordaCalculation)
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

static Task ProductionDiRegistration()
{
    var programPath = FindRepoFile("src", "ChainOfRepair.Web", "Program.cs");
    var program = File.ReadAllText(programPath);
    Assert(program.Contains("AddSingleton<ILLMClient, OpenAiLLMClient>", StringComparison.Ordinal), "expected production DI to register OpenAiLLMClient");
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
    Assert(validation.Checks.Any(c => c.Rule.Contains("Differential", StringComparison.Ordinal) && c.Passed), "expected differential validation on patched source");
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
                 "patch_prompt.txt",
                 "patch_response.json",
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

static RepairPipelineService CreatePipeline(ILLMClient client, string logRoot)
{
    return new RepairPipelineService(
        new CoRReasoningService(client, new PromptTemplateService(CreatePromptDirectory())),
        new PatchApplicationService(),
        new ValidationService(),
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
            return Task.FromResult("""{"passed":true,"concerns":[],"causalAlignment":"aligned","scopeAssessment":"method-scoped"}""");
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
