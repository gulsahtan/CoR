using System.Diagnostics;
using System.Text.Json;
using ChainOfRepair.Core.Models;
using ChainOfRepair.Core.Parsing;
using ChainOfRepair.Core.Ranking;
using ChainOfRepair.Core.Reasoning;
using ChainOfRepair.Core.Validation;

namespace ChainOfRepair.Core.Pipeline;

public sealed class RepairPipelineService(CoRReasoningService reasoningService, PatchApplicationService patchApplicationService,
    IValidationService validationService, WeightedBordaRanker ranker, ILLMClient llmClient, string? logRootDirectory = null)
{
    public async Task<RepairPipelineResult> RunAsync(RepairRequest request, CancellationToken cancellationToken = default)
    {
        var timer = Stopwatch.StartNew();
        var id = Guid.NewGuid().ToString("n");
        var directory = Path.Combine(logRootDirectory ?? "logs", "runs", id);
        Directory.CreateDirectory(directory);
        var trace = new List<PipelineTraceEntry>();
        var exchanges = new List<LlmExchange>();
        var candidates = new List<CandidateOutcome>();
        var refinements = new List<RefinementIteration>();
        var preprocessing = new PreprocessingResult(); var ranking = new RankingResult();
        SuspiciousMethodScore? selected = null;
        var validation = new ValidationResult();
        string diagnosis = "", patch = "", check = "", explanation = "", status = "unrepaired", error = "";
        var accepted = false;
        llmClient.BeginRun();
        reasoningService.BeginRun();
        await Write("input.json", request);
        try
        {
            if (request.TopK is < 1 or > 10 || request.MaxRefinements is < 0 or > 5)
                throw new ArgumentException("TopK must be 1..10 and MaxRefinements 0..5.");
            if (request.Workflow is not ("cor" or "direct" or "lexical")) throw new ArgumentException("Unknown workflow.");
            var files = request.SourceFiles.Count > 0 ? request.SourceFiles : [new SourceArtifact(request.FileName ?? "input", request.SourceCode)];
            var parsed = files.Select(f => ArtifactParserFactory.Create(request.Language).Parse(request with { FileName = f.Path, SourceCode = f.Content, SourceFiles = [] })).ToArray();
            preprocessing = new() { CandidateMethods = parsed.SelectMany(p => p.CandidateMethods).ToArray(),
                Logs = parsed.SelectMany(p => p.Logs).ToArray(), FailingOutput = FailingOutputParser.Parse(request.FailingOutput) };
            ranking = ranker.Rank(request, preprocessing);
            if (request.Workflow == "lexical")
            {
                if (string.IsNullOrWhiteSpace(request.BugDescription)) status = "ineligible";
                else
                {
                    var methods = preprocessing.CandidateMethods.ToArray();
                    var scores = WeightedBordaRanker.LexicalScores(methods, request.BugDescription);
                    ranking = ranking with { RankedMethods = methods.Select((m, i) => new SuspiciousMethodScore { Method = m, BugReportScore = scores[i], FinalSuspiciousnessScore = scores[i] })
                        .OrderByDescending(s => s.FinalSuspiciousnessScore).ThenBy(s => s.Method.Id, StringComparer.Ordinal).ToArray() };
                    status = "localization-only";
                }
            }
            await Write("ranked_methods.json", ranking);
            trace.Add(new(DateTimeOffset.UtcNow, "ranking", $"Ranked {ranking.RankedMethods.Count} methods; search Top-{request.TopK}."));
            if (request.Workflow != "lexical")
            foreach (var candidate in ranking.RankedMethods.Take(request.TopK))
            {
                selected = candidate;
                var file = files.Single(f => f.Path == candidate.Method.FilePath);
                var localRequest = request with { SourceCode = file.Content, FileName = file.Path };
                diagnosis = "{}"; patch = ""; check = "";
                if (request.Workflow == "cor")
                {
                    var exchange = await reasoningService.GenerateRootCauseAsync(localRequest, candidate, preprocessing.FailingOutput,
                        ranking.RankedMethods.Take(request.TopK).Select(s => s with { Method = s.Method with { Source = "", LocalContext = "" } }).ToArray(), cancellationToken);
                    await RecordExchanges(); diagnosis = exchange.Response;
                    using var parsedDiagnosis = JsonDocument.Parse(JsonResponse.Normalize(diagnosis));
                    var diagnosisObject = parsedDiagnosis.RootElement;
                    if (diagnosisObject.ValueKind != JsonValueKind.Object ||
                        !new[] { "faultCategory", "rootCause", "localizedMethod" }.All(key => diagnosisObject.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())) ||
                        !diagnosisObject.TryGetProperty("evidence", out var evidence) || evidence.ValueKind != JsonValueKind.Array ||
                        !diagnosisObject.TryGetProperty("confidence", out var confidence) || confidence.ValueKind != JsonValueKind.Number || !confidence.TryGetDouble(out var confidenceValue) || confidenceValue is < 0 or > 1)
                        throw new LlmCallException("DIAGNOSE response does not satisfy the required schema.");
                }
                for (var iteration = 0; iteration <= request.MaxRefinements; iteration++)
                {
                    var exchange = request.Workflow == "direct"
                        ? await reasoningService.GenerateDirectPatchAsync(localRequest, candidate.Method, patch, iteration == 0 ? "" : validation.RefinementInstruction, cancellationToken)
                        : iteration == 0
                            ? await reasoningService.GeneratePatchAsync(localRequest, candidate.Method, diagnosis, cancellationToken)
                            : await reasoningService.GenerateRefinementAsync(localRequest, candidate.Method, diagnosis, patch,
                                validation.PatchApplication, validation, iteration, cancellationToken);
                    await RecordExchanges(); patch = exchange.Response;
                    var application = patchApplicationService.Apply(localRequest, candidate.Method, patch);
                    var checkPass = true;
                    if (request.Workflow == "cor")
                    {
                        var consistency = await reasoningService.GenerateSelfConsistencyAsync(localRequest, candidate.Method, diagnosis, patch, application, cancellationToken);
                        await RecordExchanges(); check = consistency.Response; checkPass = CheckPasses(check);
                    }
                    validation = await validationService.ValidateAsync(localRequest, candidate.Method, diagnosis, patch, application, cancellationToken);
                    if (request.Workflow == "cor" && !request.DisabledValidationRules.Contains("causal-alignment") && !request.DisabledValidationRules.Contains("all"))
                    {
                        var checks = validation.Checks.Append(new ValidationCheck("causal-alignment", checkPass, "CHECK requires passed=true, consistent=true and aligned=true.")).ToArray();
                        validation = validation with { Checks = checks, Passed = checks.All(c => c.Passed),
                            RefinementInstruction = validation.RefinementInstruction + (checkPass ? "" : " | CHECK failed: " + check) };
                    }
                    candidates.Add(new(candidate.Method.Id, iteration, diagnosis, patch, check, validation, validation.Passed));
                    if (iteration > 0) refinements.Add(new() { Iteration = iteration, PatchJson = patch, SelfConsistency = check, Validation = validation });
                    await Write($"candidate_{candidates.Count:D3}.json", candidates[^1]);
                    await Write("validation_result.json", validation);
                    await File.WriteAllTextAsync(Path.Combine(directory, "patched_code.txt"), application.PatchedCode, cancellationToken);
                    if (!validation.Passed) continue;
                    accepted = true;
                    status = request.DisabledValidationRules.Count == 0 ? "framework-plausible" : "accepted-ablation";
                    // Acceptance is immutable. EXPLAIN errors/budget exhaustion never revoke a selected patch.
                    try
                    {
                        var exp = await reasoningService.GenerateExplanationAsync(localRequest, candidate.Method, diagnosis, patch, check, validation, cancellationToken);
                        await RecordExchanges(); explanation = exp.Response;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    { error = "Accepted patch retained; explanation unavailable: " + ex.Message; }
                    break;
                }
                if (accepted) break;
            }
        }
        catch (OperationCanceledException) { status = "cancelled"; error = "Repair cancelled."; }
        catch (Exception ex)
        {
            status = IsBudget(ex) ? "budget-exhausted" : IsTruncated(ex) ? "output-truncated" : ex is LlmStageException or LlmCallException or LlmConfigurationException ? "llm-error" : "failed";
            error = ex.Message;
            if (ex is LlmStageException stage)
                await Write("failed_stage.json", new { stage.Stage, stage.TemplatePath, stage.SystemPrompt, stage.UserPrompt, stage.Message });
        }
        await RecordExchanges();
        trace.Add(new(DateTimeOffset.UtcNow, "finish", status));
        var result = new RepairPipelineResult
        {
            RunId = id, Request = request, Preprocessing = preprocessing, Ranking = ranking, SelectedMethod = selected,
            Reasoning = new() { RootCause = diagnosis, PatchJson = patch, SelfConsistency = check, Explanation = explanation, Exchanges = exchanges },
            FinalValidation = validation, RefinementIterations = refinements, Candidates = candidates,
            ModelCalls = llmClient.Calls.ToArray(), DurationSeconds = timer.Elapsed.TotalSeconds, Accepted = accepted,
            FrameworkPlausible = request.DisabledValidationRules.Count > 0 || request.Workflow == "lexical" || status is "failed" or "llm-error" or "cancelled" or "output-truncated" ? null : accepted,
            FinalStatus = status, ErrorMessage = error, Trace = trace, LogDirectory = directory, LogPath = Path.Combine(directory, "final_result.json"),
            UsedRealLlmClient = llmClient is OpenAiLLMClient, MockClientUsed = llmClient.IsMock,
            LlmClientName = llmClient.ClientName, ModelName = llmClient.Settings.Model,
            PromptTemplatesUsed = exchanges.Select(e => e.TemplatePath).Distinct().ToArray()
        };
        await Write("final_result.json", result);
        return result;

        async Task Write(string name, object data) => await File.WriteAllTextAsync(Path.Combine(directory, name), JsonSerializer.Serialize(data, JsonLog.Options), CancellationToken.None);
        async Task RecordExchanges()
        {
            foreach (var exchange in reasoningService.Exchanges.Skip(exchanges.Count).ToArray())
            {
                exchanges.Add(exchange);
                await Write($"exchange_{exchanges.Count:D3}.json", exchange);
                var prefix = exchange.Stage.Replace('-', '_') + (exchange.IsComplete ? "" : $"_attempt_{exchange.Attempt}_incomplete");
                await File.WriteAllTextAsync(Path.Combine(directory, prefix + "_prompt.txt"), exchange.UserPrompt, CancellationToken.None);
                await File.WriteAllTextAsync(Path.Combine(directory, prefix + "_response." + (exchange.IsComplete && exchange.Stage != "explanation" ? "json" : "txt")), exchange.Response, CancellationToken.None);
            }
        }
    }
    private static bool IsTruncated(Exception ex) => ex is LlmOutputTruncatedException || ex.InnerException is not null && IsTruncated(ex.InnerException);
    private static bool IsBudget(Exception ex) => ex is InferenceBudgetException || ex.InnerException is not null && IsBudget(ex.InnerException);
    public static bool CheckPasses(string response)
    {
        try
        {
            using var doc = JsonDocument.Parse(JsonResponse.Normalize(response));
            return new[] { "passed", "consistent", "aligned" }.All(k => doc.RootElement.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return false; }
    }
}
