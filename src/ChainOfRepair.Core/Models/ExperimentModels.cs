namespace ChainOfRepair.Core.Models;

public sealed record SourceArtifact(string Path, string Content);
public sealed record HistoryChange(string MethodId, DateTimeOffset Timestamp, string Commit);
public sealed record ModelCall(int Number, int? InputTokens, int? OutputTokens, double Seconds,
    string? ResponseModel, string? FinishReason, string? Error)
{
    public int? CachedInputTokens { get; init; }
    public string Stage { get; init; } = "";
    public int RetryCount { get; init; }
    public int MaxOutputTokens { get; init; }
    public int? TotalTokens => InputTokens + OutputTokens;
    public decimal? EstimatedCostUsd { get; init; }
    public string? ErrorKind { get; init; }
}
public sealed record CandidateOutcome(string MethodId, int Refinement, string Diagnosis,
    string PatchJson, string CheckJson, ValidationResult Validation, bool Accepted);
public sealed record CommandSpec
{
    public string Executable { get; init; } = "";
    public string[] Arguments { get; init; } = [];
    public int TimeoutSeconds { get; init; } = 120;
}
public sealed record CommandOutcome(int? ExitCode, string Output, double Seconds, string? Error)
{
    public bool Passed => ExitCode == 0 && Error is null;
}
public sealed record StaticFinding(string Tool, string CheckId, string File, string Method,
    string Category, int Severity);
public sealed record ValidationConfiguration
{
    public string? ProjectDirectory { get; init; }
    public CommandSpec? Build { get; init; }
    // Must emit a JSON array of StaticFinding on stdout, including [] when clean.
    public CommandSpec? StaticAnalysis { get; init; }
    public int RejectionSeverity { get; init; } = 2;
}
public sealed record ExperimentCase
{
    public string ProjectName { get; init; } = "";
    public string? SetupError { get; init; }
    public string? IndependentEvaluationDirectory { get; init; }
    public CommandSpec? IndependentTests { get; init; }
    public string? RcaReviewsFile { get; init; }
    public bool RequireBaselineFailure { get; init; }

    public string Benchmark { get; init; } = "";
    public string BugId { get; init; } = "";
    public string Revision { get; init; } = "";
    public string Split { get; init; } = "evaluation";
    public SupportedLanguage Language { get; init; }
    public string ProjectDirectory { get; init; } = "";
    public string[] SourceFiles { get; init; } = [];
    public string? BugReportFile { get; init; }
    public string? StackTraceFile { get; init; }
    public string? HistoryFile { get; init; }
    public DateTimeOffset? DefectTime { get; init; }
    public CommandSpec? Build { get; init; }
    public CommandSpec? StaticAnalysis { get; init; }
    // Evaluation metadata is never part of RepairRequest or a model prompt.
    public string? ReferenceMethodsFile { get; init; }
    public string? EvaluationDirectory { get; init; }
    public CommandSpec? RetrospectiveTests { get; init; }
}
public sealed record ExperimentConfiguration
{
    public string Name { get; init; } = "full-cor";
    public int TopK { get; init; } = 5;
    public int MaxRefinements { get; init; } = 3;
    public string Workflow { get; init; } = "cor";
    public ArtifactWeights Weights { get; init; } = ArtifactWeights.Defaults;
    public bool UseTrace { get; init; } = true;
    public bool UseReport { get; init; } = true;
    public bool UseHistory { get; init; } = true;
    public string[] DisabledValidationRules { get; init; } = [];
    public int HistoryWindowDays { get; init; } = 365;
    public double HistoryDecayPerDay { get; init; } = 0.01;
}
public sealed record ExperimentManifest
{
    public ApiPricing? Pricing { get; init; }
    public string SelectionProtocol { get; init; } = "";
    public int SchemaVersion { get; init; } = 1;
    public LlmSettings Model { get; init; } = new();
    public int Repeats { get; init; } = 1;
    public ExperimentConfiguration[] Configurations { get; init; } = [new()];
    public ExperimentCase[] Cases { get; init; } = [];
}
public sealed record ApiPricing(string Model, decimal InputPerMillion, decimal CachedInputPerMillion,
    decimal OutputPerMillion, string Source, string CheckedOn);
public sealed record RcaReview(string ResponseHash, bool Correct, string Reviewer, string Evidence);
public sealed record ExperimentRecord
{
    public string? PromptVersion { get; init; }
    public string ProjectName { get; init; } = "";
    public bool? Top3 { get; init; }
    public bool? RootCauseCorrect { get; init; }
    public bool? IndependentlyVerified { get; init; }
    public CommandOutcome? IndependentTests { get; init; }
    public CommandOutcome? BaselineTests { get; init; }
    public double ExperimentSeconds { get; init; }
    public string TestRole { get; init; } = "retrospective benchmark tests; not independent";

    public string RunId { get; init; } = "";
    public string Benchmark { get; init; } = "";
    public string BugId { get; init; } = "";
    public string Revision { get; init; } = "";
    public string Split { get; init; } = "";
    public string Configuration { get; init; } = "";
    public int Repeat { get; init; }
    public int ExpectedRepeats { get; init; } = 1;
    public string ManifestHash { get; init; } = "";
    public IReadOnlyDictionary<string, string> SourceHashes { get; init; } = new Dictionary<string, string>();
    public bool? Top1 { get; init; }
    public bool? Top5 { get; init; }
    public bool? SearchHit { get; init; }
    public bool? CorrectRepair { get; init; }
    public CommandOutcome? Tests { get; init; }
    public string? EvaluationError { get; init; }
    public RepairPipelineResult Result { get; init; } = new();
}
