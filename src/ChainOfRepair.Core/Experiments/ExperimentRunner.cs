using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChainOfRepair.Core.Models;
using ChainOfRepair.Core.Pipeline;
using ChainOfRepair.Core.Ranking;
using ChainOfRepair.Core.Reasoning;
using ChainOfRepair.Core.Validation;

namespace ChainOfRepair.Core.Experiments;

public static class ExperimentRunner
{
    public static async Task<int> RunAsync(string manifestPath, string output, string prompts, string? caseFilter = null,
        string? configFilter = null, CancellationToken cancellationToken = default)
    {
        manifestPath = Path.GetFullPath(manifestPath);
        var text = await File.ReadAllTextAsync(manifestPath, cancellationToken);
        var manifest = JsonSerializer.Deserialize<ExperimentManifest>(text, JsonLog.Options) ?? throw new InvalidDataException("Empty manifest.");
        ValidateManifest(manifest);
        var root = Path.GetDirectoryName(manifestPath)!;
        string Resolve(string path) => Path.GetFullPath(path, root);
        var cases = manifest.Cases.Where(c => caseFilter is null || c.BugId == caseFilter).ToArray();
        var configs = manifest.Configurations.Where(c => configFilter is null || c.Name == configFilter).ToArray();
        if (cases.Length == 0 || configs.Length == 0) throw new InvalidDataException("No cases/configurations matched.");
        Directory.CreateDirectory(output);
        var batch = Path.Combine(output, DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmss") + "-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(batch);
        await File.WriteAllTextAsync(Path.Combine(batch, "manifest.json"), text, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(batch, "environment.json"), JsonSerializer.Serialize(new {
            runtime = Environment.Version.ToString(), os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            sdk = typeof(OpenAI.OpenAIClient).Assembly.GetName().Version?.ToString(), timestamp = DateTimeOffset.UtcNow,
            git = await CommandRunner.RunAsync(new() { Executable = "git", Arguments = ["rev-parse", "HEAD"] }, Directory.GetCurrentDirectory(), "", cancellationToken),
            workingTree = await CommandRunner.RunAsync(new() { Executable = "git", Arguments = ["diff", "--stat"] }, Directory.GetCurrentDirectory(), "", cancellationToken)
        }, JsonLog.Options), cancellationToken);
        await ResearchReports.SaveConfigAsync(manifest, batch, prompts, cases, configs, root, cancellationToken);
        using var frozenConfig = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(batch, "experiment_config.json"), cancellationToken));
        var promptVersion = frozenConfig.RootElement.GetProperty("promptVersion").GetString();
        var batchTimer = Stopwatch.StartNew();
        var records = new List<ExperimentRecord>();
        foreach (var item in cases)
        foreach (var config in configs)
        for (var repeat = 1; repeat <= manifest.Repeats; repeat++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Console.WriteLine($"{item.Benchmark}/{item.BugId}: {config.Name}, repeat {repeat}");
            var timer = Stopwatch.StartNew();
            CommandOutcome? baseline = null;
            RepairPipelineResult? completedResult = null;
            ExperimentRecord record;
            try
            {
                if (item.SetupError is not null) throw new InvalidDataException(item.SetupError);
                if (config.Workflow != "lexical" && !config.DisabledValidationRules.Contains("all") && !config.DisabledValidationRules.Contains("static") && item.StaticAnalysis is null)
                    throw new InvalidDataException("Configure a real static analyzer before spending inference budget on this case.");
                var project = Resolve(item.ProjectDirectory);
                var sources = item.SourceFiles.Select(path => new SourceArtifact(path.Replace('\\', '/'),
                    File.ReadAllText(IsolatedProject.SafePath(project, path)))).ToArray();
                var history = config.UseHistory && item.HistoryFile is not null
                    ? JsonSerializer.Deserialize<HistoryChange[]>(File.ReadAllText(Resolve(item.HistoryFile)), JsonLog.Options) : null;
                var request = new RepairRequest
                {
                    SourceFiles = sources, FileName = sources[0].Path, SourceCode = sources[0].Content, Language = item.Language,
                    FailingOutput = config.UseTrace && item.StackTraceFile is not null ? File.ReadAllText(Resolve(item.StackTraceFile)) : "",
                    BugDescription = config.UseReport && item.BugReportFile is not null ? File.ReadAllText(Resolve(item.BugReportFile)) : null,
                    RevisionHistory = history, DefectTime = item.DefectTime, Weights = config.Weights, TopK = config.TopK,
                    MaxRefinements = config.MaxRefinements, Workflow = config.Workflow, DisabledValidationRules = config.DisabledValidationRules,
                    HistoryWindowDays = config.HistoryWindowDays, HistoryDecayPerDay = config.HistoryDecayPerDay
                };
                if (item.RequireBaselineFailure)
                {
                    if (item.RetrospectiveTests is null || item.EvaluationDirectory is null)
                        throw new InvalidDataException("Baseline reproduction requires an evaluation directory and test command.");
                    using var originalEvaluation = new IsolatedProject(Resolve(item.EvaluationDirectory), request, request.SourceCode);
                    baseline = await CommandRunner.RunAsync(item.RetrospectiveTests, originalEvaluation.DirectoryPath, request.FileName!, cancellationToken);
                    if (baseline.ExitCode != 1) throw new InvalidDataException("Original benchmark failure was not reproduced; no inference dispatched. " + baseline.Error);
                }
                var client = new OpenAiLLMClient(manifest.Model);
                var pipeline = new RepairPipelineService(new(client, new(prompts)), new(),
                    new ValidationService(new() { ProjectDirectory = project, Build = item.Build, StaticAnalysis = item.StaticAnalysis }),
                    new(), client, batch);
                var result = await pipeline.RunAsync(request, cancellationToken);
                completedResult = result;
                bool? top1 = null, top3 = null, top5 = null, searchHit = null, correct = null;
                CommandOutcome? tests = null;
                string? evaluationError = null;
                // The repair has terminated. Nothing below can feed back into selection/refinement.
                try
                {
                    if (item.ReferenceMethodsFile is not null && result.Ranking.RankedMethods.Count > 0 && result.FinalStatus != "ineligible")
                    {
                        var reference = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Resolve(item.ReferenceMethodsFile)), JsonLog.Options)
                            ?? throw new InvalidDataException("Missing reference methods.");
                        if (reference.Length == 0) throw new InvalidDataException("Reference method labels are empty.");
                        top1 = result.Ranking.RankedMethods.Take(1).Any(m => reference.Contains(m.Method.Id, StringComparer.Ordinal));
                        top3 = result.Ranking.RankedMethods.Take(3).Any(m => reference.Contains(m.Method.Id, StringComparer.Ordinal));
                        top5 = result.Ranking.RankedMethods.Take(5).Any(m => reference.Contains(m.Method.Id, StringComparer.Ordinal));
                        searchHit = result.Ranking.RankedMethods.Take(config.TopK).Any(m => reference.Contains(m.Method.Id, StringComparer.Ordinal));
                    }
                }
                catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException) { evaluationError = ex.Message; }
                if (config.Workflow != "lexical")
                {
                    if (!result.Accepted && result.FinalStatus is "unrepaired" or "budget-exhausted") correct = false;
                    else if (result.Accepted && item.RetrospectiveTests is not null && item.EvaluationDirectory is not null)
                    {
                        try
                        {
                            var chosen = result.SelectedMethod!.Method;
                            using var evaluation = new IsolatedProject(Resolve(item.EvaluationDirectory), request with { FileName = chosen.FilePath }, result.FinalValidation.PatchApplication.PatchedCode);
                            tests = await CommandRunner.RunAsync(item.RetrospectiveTests, evaluation.DirectoryPath, chosen.FilePath, cancellationToken);
                            // Oracle contract: 0=all tests passed, 1=behavioral failure, other/missing=tool error.
                            correct = tests.ExitCode == 0 && tests.Error is null ? true : tests.ExitCode == 1 ? false : null;
                        }
                        catch (Exception ex) when (ex is IOException or InvalidDataException) { evaluationError = (evaluationError + " " + ex.Message).Trim(); }
                    }
                }
                CommandOutcome? independent = null;
                bool? independentlyVerified = null, rcaCorrect = null;
                if (result.Accepted && tests?.Passed == true && item.IndependentTests is not null && item.IndependentEvaluationDirectory is not null)
                {
                    using var verification = new IsolatedProject(Resolve(item.IndependentEvaluationDirectory), request with { FileName = result.SelectedMethod!.Method.FilePath }, result.FinalValidation.PatchApplication.PatchedCode);
                    independent = await CommandRunner.RunAsync(item.IndependentTests, verification.DirectoryPath, result.SelectedMethod.Method.FilePath, cancellationToken);
                    independentlyVerified = independent.ExitCode == 0 && independent.Error is null ? true : independent.ExitCode == 1 ? false : null;
                }
                if (item.RcaReviewsFile is not null)
                {
                    var reviews = JsonSerializer.Deserialize<RcaReview[]>(File.ReadAllText(Resolve(item.RcaReviewsFile)), JsonLog.Options) ?? [];
                    var match = reviews.SingleOrDefault(r => r.ResponseHash == Hash(result.Reasoning.RootCause) && !string.IsNullOrWhiteSpace(r.Reviewer) && !string.IsNullOrWhiteSpace(r.Evidence));
                    rcaCorrect = match?.Correct;
                }
                result = result with { ModelCalls = result.ModelCalls.Select(c => c with { EstimatedCostUsd = ResearchReports.Cost(c, manifest.Pricing) }).ToArray() };
                record = new()
                {
                    RunId = result.RunId, Benchmark = item.Benchmark, BugId = item.BugId, Revision = item.Revision, Split = item.Split,
                    Configuration = config.Name, Repeat = repeat, ExpectedRepeats = manifest.Repeats, ManifestHash = Hash(text),
                    SourceHashes = sources.ToDictionary(f => f.Path, f => Hash(f.Content)),
                    ProjectName = item.ProjectName, Top3 = top3, RootCauseCorrect = rcaCorrect,
                    IndependentlyVerified = independentlyVerified, IndependentTests = independent,
                    Top1 = top1, Top5 = top5, SearchHit = searchHit, CorrectRepair = correct, Tests = tests, EvaluationError = evaluationError, Result = result
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var id = Guid.NewGuid().ToString("n");
                record = new() { ProjectName = item.ProjectName, RunId = id, Benchmark = item.Benchmark, BugId = item.BugId, Revision = item.Revision,
                    Split = item.Split, Configuration = config.Name, Repeat = repeat, ExpectedRepeats = manifest.Repeats, ManifestHash = Hash(text),
                    Result = completedResult is null ? new() { RunId = id, FinalStatus = "setup-error", FrameworkPlausible = null, ErrorMessage = ex.Message }
                        : completedResult with { ModelCalls = completedResult.ModelCalls.Select(c => c with { EstimatedCostUsd = ResearchReports.Cost(c, manifest.Pricing) }).ToArray() },
                    EvaluationError = completedResult is null ? null : "Post-repair evaluation error: " + ex.Message };
                if (completedResult is not null) record = record with { RunId = completedResult.RunId };
            }
            record = record with { PromptVersion = promptVersion, BaselineTests = baseline, ExperimentSeconds = timer.Elapsed.TotalSeconds };
            records.Add(record);
            await File.WriteAllTextAsync(Path.Combine(batch, record.RunId + ".record.json"), JsonSerializer.Serialize(record, JsonLog.Options), cancellationToken);
            await ExperimentReports.WriteAsync(records, batch, cancellationToken);
        }
        await File.WriteAllTextAsync(Path.Combine(batch, "batch_timing.json"), JsonSerializer.Serialize(new { seconds = batchTimer.Elapsed.TotalSeconds, completed = records.Count }, JsonLog.Options), cancellationToken);
        Console.WriteLine(await File.ReadAllTextAsync(Path.Combine(batch, "report.md"), cancellationToken));
        Console.WriteLine($"Experiment records: {Path.GetFullPath(batch)}");
        return records.Any(r => r.Result.FinalStatus is "setup-error" or "failed" or "llm-error" or "output-truncated" or "cancelled" || r.EvaluationError is not null) ? 2 : 0;
    }

    public static void ValidateManifest(ExperimentManifest m)
    {
        if (m.SchemaVersion != 1 || m.Repeats < 1 || m.Cases.Length == 0 || m.Configurations.Length == 0)
            throw new InvalidDataException("Manifest requires schemaVersion=1, repeats>=1, nonempty cases and configurations.");
        if (m.Pricing is { } price && (price.InputPerMillion < 0 || price.OutputPerMillion < 0 || price.CachedInputPerMillion < 0 || string.IsNullOrWhiteSpace(price.Source)))
            throw new InvalidDataException("Pricing must have a source and nonnegative USD per-million rates.");
        if (m.Model.UseMockClient) throw new InvalidDataException("Mock clients are forbidden in experiments.");
        if (m.Configurations.Select(c => c.Name).Distinct().Count() != m.Configurations.Length) throw new InvalidDataException("Duplicate configuration names.");
        if (m.Cases.GroupBy(c => (c.Benchmark, c.BugId)).Any(g => g.Count() > 1))
            throw new InvalidDataException("Duplicate defect IDs or calibration/evaluation overlap.");
        foreach (var c in m.Cases)
        {
            if (c.Benchmark is not ("Defects4J" or "QuixBugs" or "IntroClass" or "LocalExample") || string.IsNullOrWhiteSpace(c.BugId) || string.IsNullOrWhiteSpace(c.Revision) || c.Revision.Contains("REPLACE", StringComparison.Ordinal) || c.BugId.Contains("REPLACE", StringComparison.Ordinal))
                throw new InvalidDataException("Each case requires a supported benchmark, bugId and pinned revision.");
            if (c.Split is not ("calibration" or "evaluation" or "pilot") || c.SourceFiles.Length == 0 || c.SourceFiles.Distinct().Count() != c.SourceFiles.Length)
                throw new InvalidDataException("Invalid split or source file list.");
            if (c.EvaluationDirectory is not null && c.EvaluationDirectory == c.ProjectDirectory)
                throw new InvalidDataException("Repair and evaluation directories must be separate.");
            if (c.IndependentEvaluationDirectory is not null && (Path.GetFullPath(c.IndependentEvaluationDirectory) == Path.GetFullPath(c.ProjectDirectory) || (c.EvaluationDirectory is not null && Path.GetFullPath(c.IndependentEvaluationDirectory) == Path.GetFullPath(c.EvaluationDirectory))))
                throw new InvalidDataException("Independent verification requires a separate directory and independent test provenance.");
            foreach (var path in c.SourceFiles) IsolatedProject.SafePath(Path.GetTempPath(), path);
        }
        foreach (var c in m.Configurations)
        {
            if (c.TopK is < 1 or > 10 || c.MaxRefinements is < 0 or > 5 || c.Workflow is not ("cor" or "direct" or "lexical"))
                throw new InvalidDataException("Invalid workflow, TopK or refinement limit.");
            var known = new[] { "all", "compilation", "static", "interface", "semantic", "null-safety", "boundary", "control-flow", "type-consistency", "causal-alignment" };
            if (c.DisabledValidationRules.Any(r => !known.Contains(r))) throw new InvalidDataException("Unknown disabled validation rule.");
            new WeightedBordaRanker().NormalizeWeights(c.Weights, true, true, true, true);
        }
    }
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
