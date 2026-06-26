using System.Text.Json;
using ChainOfRepair.Core.Models;
using ChainOfRepair.Core.Parsing;
using ChainOfRepair.Core.Ranking;
using ChainOfRepair.Core.Reasoning;
using ChainOfRepair.Core.Validation;

namespace ChainOfRepair.Core.Pipeline;

public sealed class RepairPipelineService
{
    private readonly CoRReasoningService _reasoningService;
    private readonly PatchApplicationService _patchApplicationService;
    private readonly ValidationService _validationService;
    private readonly WeightedBordaRanker _ranker;
    private readonly ILLMClient _llmClient;
    private readonly string _logRootDirectory;

    public RepairPipelineService(
        CoRReasoningService reasoningService,
        PatchApplicationService patchApplicationService,
        ValidationService validationService,
        WeightedBordaRanker ranker,
        ILLMClient llmClient,
        string? logRootDirectory = null)
    {
        _reasoningService = reasoningService;
        _patchApplicationService = patchApplicationService;
        _validationService = validationService;
        _ranker = ranker;
        _llmClient = llmClient;
        _logRootDirectory = logRootDirectory ?? Path.Combine(AppContext.BaseDirectory, "logs");
    }

    public async Task<RepairPipelineResult> RunAsync(RepairRequest request, CancellationToken cancellationToken = default)
    {
        var trace = new List<PipelineTraceEntry>();
        var runId = Guid.NewGuid().ToString("n");
        var runDirectory = Path.Combine(_logRootDirectory, "runs", runId);
        Directory.CreateDirectory(runDirectory);

        Trace(trace, "start", "Repair pipeline started.");
        var normalizedRequest = request with { TopK = NormalizeTopK(request.TopK) };
        await WriteJsonAsync(runDirectory, "input.json", normalizedRequest, cancellationToken);

        PreprocessingResult preprocessing;
        RankingResult ranking;
        try
        {
            var parser = ArtifactParserFactory.Create(normalizedRequest.Language);
            preprocessing = parser.Parse(normalizedRequest);
            Trace(trace, "preprocessing", $"Extracted {preprocessing.CandidateMethods.Count} candidate method/function artifact(s).");

            ranking = _ranker.Rank(normalizedRequest, preprocessing);
            Trace(trace, "ranking", $"Computed artifact-aware Weighted Borda ranking and selected top {ranking.RankedMethods.Count} candidate(s).");
            await WriteJsonAsync(runDirectory, "ranked_methods.json", ranking, cancellationToken);
        }
        catch (Exception ex)
        {
            var failed = BuildBaseResult(runId, runDirectory, normalizedRequest, trace) with
            {
                FinalStatus = "failed",
                ErrorMessage = "Preprocessing or ranking failed: " + ex.Message
            };
            await WriteJsonAsync(runDirectory, "final_result.json", failed, cancellationToken);
            return failed with { LogPath = Path.Combine(runDirectory, "final_result.json") };
        }

        var selected = ranking.RankedMethods.FirstOrDefault();
        if (selected is null)
        {
            var empty = BuildBaseResult(runId, runDirectory, normalizedRequest, trace) with
            {
                Preprocessing = preprocessing,
                Ranking = ranking,
                FinalStatus = "unrepaired",
                ErrorMessage = "No candidate method/function could be extracted from the input source."
            };
            await WriteJsonAsync(runDirectory, "final_result.json", empty, cancellationToken);
            return empty with { LogPath = Path.Combine(runDirectory, "final_result.json") };
        }

        var exchanges = new List<LlmExchange>();
        var refinements = new List<RefinementIteration>();

        try
        {
            var rootCause = await _reasoningService.GenerateRootCauseAsync(
                normalizedRequest,
                selected,
                preprocessing.FailingOutput,
                ranking.RankedMethods,
                cancellationToken);
            exchanges.Add(rootCause);
            await WriteExchangeAsync(runDirectory, "root_cause", rootCause, responseFileExtension: ".json", cancellationToken);
            EnsureJsonStage(rootCause.Response, "root-cause");
            Trace(trace, "root-cause", "Received dynamic root-cause JSON from the LLM.");

            var patch = await _reasoningService.GeneratePatchAsync(normalizedRequest, selected.Method, rootCause.Response, cancellationToken);
            exchanges.Add(patch);
            await WriteExchangeAsync(runDirectory, "patch", patch, responseFileExtension: ".json", cancellationToken);
            EnsureJsonStage(patch.Response, "patch-synthesis");
            Trace(trace, "patch-synthesis", "Received dynamic patch JSON from the LLM.");

            var application = _patchApplicationService.Apply(normalizedRequest, selected.Method, patch.Response);
            var consistency = await _reasoningService.GenerateSelfConsistencyAsync(
                normalizedRequest,
                selected.Method,
                rootCause.Response,
                patch.Response,
                application,
                cancellationToken);
            exchanges.Add(consistency);
            await WriteExchangeAsync(runDirectory, "self_consistency", consistency, responseFileExtension: ".json", cancellationToken);
            Trace(trace, "self-consistency", "Received dynamic self-consistency result from the LLM.");

            await File.WriteAllTextAsync(Path.Combine(runDirectory, "patched_code.txt"), application.PatchedCode, cancellationToken);
            var validation = await _validationService.ValidateAsync(normalizedRequest, selected.Method, rootCause.Response, patch.Response, application, cancellationToken);
            await WriteJsonAsync(runDirectory, "validation_result.json", validation, cancellationToken);
            Trace(trace, "validation", validation.Passed ? "Initial patch passed validation." : "Initial patch failed validation; entering refinement loop.");

            var finalPatch = patch;
            var finalConsistency = consistency;
            var finalValidation = validation;

            for (var iteration = 1; !finalValidation.Passed && iteration <= 3; iteration++)
            {
                var refinement = await _reasoningService.GenerateRefinementAsync(
                    normalizedRequest,
                    selected.Method,
                    rootCause.Response,
                    finalPatch.Response,
                    finalValidation.PatchApplication,
                    finalValidation,
                    iteration,
                    cancellationToken);
                exchanges.Add(refinement);
                await WriteExchangeAsync(runDirectory, $"refinement_{iteration}", refinement, responseFileExtension: ".json", cancellationToken);
                EnsureJsonStage(refinement.Response, $"refinement-{iteration}");

                var refinedApplication = _patchApplicationService.Apply(normalizedRequest, selected.Method, refinement.Response);
                var refinedConsistency = await _reasoningService.GenerateSelfConsistencyAsync(
                    normalizedRequest,
                    selected.Method,
                    rootCause.Response,
                    refinement.Response,
                    refinedApplication,
                    cancellationToken);
                exchanges.Add(refinedConsistency);
                await WriteExchangeAsync(runDirectory, $"refinement_{iteration}_self_consistency", refinedConsistency, responseFileExtension: ".json", cancellationToken);

                finalValidation = await _validationService.ValidateAsync(normalizedRequest, selected.Method, rootCause.Response, refinement.Response, refinedApplication, cancellationToken);
                await WriteJsonAsync(runDirectory, $"refinement_{iteration}_validation_result.json", finalValidation, cancellationToken);

                finalPatch = refinement;
                finalConsistency = refinedConsistency;
                refinements.Add(new RefinementIteration
                {
                    Iteration = iteration,
                    PatchJson = refinement.Response,
                    SelfConsistency = refinedConsistency.Response,
                    Validation = finalValidation,
                    Exchanges = [refinement, refinedConsistency]
                });

                await File.WriteAllTextAsync(Path.Combine(runDirectory, "patched_code.txt"), refinedApplication.PatchedCode, cancellationToken);
                Trace(trace, "refinement", $"Iteration {iteration} {(finalValidation.Passed ? "passed" : "failed")} validation.");
            }

            var explanation = await _reasoningService.GenerateExplanationAsync(
                normalizedRequest,
                selected.Method,
                rootCause.Response,
                finalPatch.Response,
                finalConsistency.Response,
                finalValidation,
                cancellationToken);
            exchanges.Add(explanation);
            await WriteExchangeAsync(runDirectory, "explanation", explanation, responseFileExtension: ".txt", cancellationToken);
            Trace(trace, "explanation", "Received dynamic developer explanation from the LLM.");

            var reasoning = new ReasoningResult
            {
                RootCause = rootCause.Response,
                PatchJson = finalPatch.Response,
                SelfConsistency = finalConsistency.Response,
                Explanation = explanation.Response,
                Exchanges = exchanges
            };

            var result = BuildBaseResult(runId, runDirectory, normalizedRequest, trace) with
            {
                Preprocessing = preprocessing,
                Ranking = ranking,
                SelectedMethod = selected,
                Reasoning = reasoning,
                FinalValidation = finalValidation,
                RefinementIterations = refinements,
                FinalStatus = finalValidation.Passed ? "repaired" : "unrepaired",
                PromptTemplatesUsed = exchanges.Select(exchange => exchange.TemplatePath).Distinct().ToArray()
            };

            Trace(trace, "finish", $"Repair pipeline finished with status: {result.FinalStatus}.");
            await WriteJsonAsync(runDirectory, "final_result.json", result, cancellationToken);
            return result with { LogPath = Path.Combine(runDirectory, "final_result.json") };
        }
        catch (LlmStageException ex)
        {
            await WritePromptOnlyAsync(runDirectory, ex, cancellationToken);
            var failed = BuildBaseResult(runId, runDirectory, normalizedRequest, trace) with
            {
                Preprocessing = preprocessing,
                Ranking = ranking,
                SelectedMethod = selected,
                Reasoning = new ReasoningResult { Exchanges = exchanges },
                RefinementIterations = refinements,
                FinalStatus = "llm-error",
                ErrorMessage = $"{ex.Stage} LLM call failed: {ex.Message}",
                PromptTemplatesUsed = exchanges.Select(exchange => exchange.TemplatePath).Append(ex.TemplatePath).Distinct().ToArray()
            };
            Trace(trace, "llm-error", failed.ErrorMessage);
            await WriteJsonAsync(runDirectory, "final_result.json", failed, cancellationToken);
            return failed with { LogPath = Path.Combine(runDirectory, "final_result.json") };
        }
        catch (Exception ex)
        {
            var failed = BuildBaseResult(runId, runDirectory, normalizedRequest, trace) with
            {
                Preprocessing = preprocessing,
                Ranking = ranking,
                SelectedMethod = selected,
                Reasoning = new ReasoningResult { Exchanges = exchanges },
                RefinementIterations = refinements,
                FinalStatus = "failed",
                ErrorMessage = ex.Message,
                PromptTemplatesUsed = exchanges.Select(exchange => exchange.TemplatePath).Distinct().ToArray()
            };
            Trace(trace, "failed", ex.Message);
            await WriteJsonAsync(runDirectory, "final_result.json", failed, cancellationToken);
            return failed with { LogPath = Path.Combine(runDirectory, "final_result.json") };
        }
    }

    private RepairPipelineResult BuildBaseResult(string runId, string runDirectory, RepairRequest request, IReadOnlyList<PipelineTraceEntry> trace) =>
        new()
        {
            RunId = runId,
            Request = request,
            Trace = trace,
            LogDirectory = runDirectory,
            UsedRealLlmClient = !_llmClient.IsMock,
            MockClientUsed = _llmClient.IsMock,
            LlmClientName = _llmClient.ClientName,
            ModelName = _llmClient.Settings.Model
        };

    private static async Task WriteExchangeAsync(
        string runDirectory,
        string filePrefix,
        LlmExchange exchange,
        string responseFileExtension,
        CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(Path.Combine(runDirectory, $"{filePrefix}_prompt.txt"), exchange.UserPrompt, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(runDirectory, $"{filePrefix}_response{responseFileExtension}"), exchange.Response, cancellationToken);
    }

    private static async Task WritePromptOnlyAsync(string runDirectory, LlmStageException exception, CancellationToken cancellationToken)
    {
        var prefix = exception.Stage switch
        {
            "root-cause" => "root_cause",
            "patch-synthesis" => "patch",
            "self-consistency" => "self_consistency",
            "explanation" => "explanation",
            "refinement" => "refinement_failed",
            _ => exception.Stage.Replace('-', '_')
        };
        await File.WriteAllTextAsync(Path.Combine(runDirectory, $"{prefix}_prompt.txt"), exception.UserPrompt, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(runDirectory, $"{prefix}_error.txt"), exception.Message, cancellationToken);
    }

    private static async Task WriteJsonAsync(string runDirectory, string fileName, object value, CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(
            Path.Combine(runDirectory, fileName),
            JsonSerializer.Serialize(value, JsonLog.Options),
            cancellationToken);
    }

    private static void EnsureJsonStage(string response, string stage)
    {
        try
        {
            using var _ = JsonDocument.Parse(JsonResponse.Normalize(response));
        }
        catch (JsonException ex)
        {
            throw new LlmCallException($"The {stage} LLM response was not valid JSON: {ex.Message}", ex);
        }
    }

    private static void Trace(List<PipelineTraceEntry> trace, string stage, string message) =>
        trace.Add(new PipelineTraceEntry(DateTimeOffset.UtcNow, stage, message));

    private static int NormalizeTopK(int topK) => topK is 3 or 5 or 10 ? topK : 5;
}
