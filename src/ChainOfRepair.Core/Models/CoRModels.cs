using System.Text.Json.Serialization;

namespace ChainOfRepair.Core.Models;

public enum SupportedLanguage
{
    Java,
    Python,
    C,
    CSharp
}

public sealed record RepairRequest
{
    public IReadOnlyList<SourceArtifact> SourceFiles { get; init; } = [];
    public IReadOnlyList<HistoryChange>? RevisionHistory { get; init; }
    public DateTimeOffset? DefectTime { get; init; }
    public int HistoryWindowDays { get; init; } = 365;
    public double HistoryDecayPerDay { get; init; } = 0.01;
    public ArtifactWeights Weights { get; init; } = ArtifactWeights.Defaults;
    public int MaxRefinements { get; init; } = 3;
    public string Workflow { get; init; } = "cor";
    public IReadOnlyList<string> DisabledValidationRules { get; init; } = [];
    public string SourceCode { get; init; } = "";
    public string FailingOutput { get; init; } = "";
    public string? BugDescription { get; init; }
    public SupportedLanguage Language { get; init; } = SupportedLanguage.Java;
    public int TopK { get; init; } = 5;
    public string? FileName { get; init; }
}

public sealed record CandidateMethod
{
    public string FilePath { get; init; } = "";
    public string Signature { get; init; } = "";
    public string Id => $"{FilePath}::{Context}.{Signature}";
    public string Name { get; init; } = "unknown";
    public string Context { get; init; } = "input";
    public int StartLine { get; init; }
    public int EndLine { get; init; }
    public string Source { get; init; } = "";
    public string LocalContext { get; init; } = "";
    public IReadOnlyList<string> ControlFlowIndicators { get; init; } = [];
    public IReadOnlyList<string> SuspiciousConstructs { get; init; } = [];
}

public sealed record StackTraceFrameInfo(string? FileName, string? MethodName, int? LineNumber, string Raw);

public sealed record CompilerDiagnostic(string Kind, string? FileName, int? LineNumber, string? Code, string Message, string Raw);

public sealed record FailingOutputAnalysis
{
    public IReadOnlyList<StackTraceFrameInfo> StackTraceFrames { get; init; } = [];
    public IReadOnlyList<CompilerDiagnostic> CompilerErrors { get; init; } = [];
    public IReadOnlyList<CompilerDiagnostic> Warnings { get; init; } = [];
    public IReadOnlyList<string> FileNames { get; init; } = [];
    public IReadOnlyList<string> MethodNames { get; init; } = [];
    public IReadOnlyList<int> LineNumbers { get; init; } = [];
    public IReadOnlyList<string> ErrorTypes { get; init; } = [];
}

public sealed record PreprocessingLogEntry(DateTimeOffset Timestamp, string Step, string Message, object? Data = null);

public sealed record PreprocessingResult
{
    public IReadOnlyList<CandidateMethod> CandidateMethods { get; init; } = [];
    public FailingOutputAnalysis FailingOutput { get; init; } = new();
    public IReadOnlyList<PreprocessingLogEntry> Logs { get; init; } = [];
}

public sealed record ArtifactWeights(double SourceContext, double FailingOutput, double BugReport, double RevisionHistory)
{
    public static ArtifactWeights Defaults => new(0.40, 0.30, 0.20, 0.10);
}

public sealed record SuspiciousMethodScore
{
    public CandidateMethod Method { get; init; } = new();
    public double SourceCodeScore { get; init; }
    public double StackTraceScore { get; init; }
    public double BugReportScore { get; init; }
    public double RevisionHistoryScore { get; init; }
    public double FinalSuspiciousnessScore { get; init; }
    public string Explanation { get; init; } = "";
}

public sealed record RankingResult
{
    public ArtifactWeights NormalizedWeights { get; init; } = ArtifactWeights.Defaults;
    public IReadOnlyList<SuspiciousMethodScore> RankedMethods { get; init; } = [];
}

public sealed record StageTokenLimits
{
    public int RootCause { get; init; } = 512;
    public int Patch { get; init; } = 2048;
    public int Refinement { get; init; } = 2048;
    public int DirectPatch { get; init; } = 2048;
    public int SelfConsistency { get; init; } = 512;
    public int Explanation { get; init; } = 384;
    public int PatchLengthRetry { get; init; } = 4096;
}

public sealed record LlmSettings
{
    public int TotalTokenBudget { get; init; } = 16000;
    public int MaxCalls { get; init; } = 20;
    public string Provider { get; init; } = "OpenAI";
    public string Model { get; init; } = "gpt-4o";
    public float Temperature { get; init; } = 0.2f;
    // Absolute per-call ceiling, including the one permitted patch-length retry.
    public int MaxTokens { get; init; } = 2048;
    public StageTokenLimits OutputLimits { get; init; } = new();
    public bool UseMockClient { get; init; }
}

public sealed record LlmExchange
{
    public string? ErrorKind { get; init; }
    public bool IsComplete { get; init; } = true;
    public string? FinishReason { get; init; }
    public int Attempt { get; init; } = 1;
    public string PromptHash { get; init; } = "";
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string Stage { get; init; } = "";
    public string Client { get; init; } = "";
    public string Model { get; init; } = "";
    public float Temperature { get; init; }
    public int MaxTokens { get; init; }
    public string TemplatePath { get; init; } = "";
    public string SystemPrompt { get; init; } = "";
    public string UserPrompt { get; init; } = "";
    public string Response { get; init; } = "";
    public bool MockClientUsed { get; init; }
}

public sealed record ReasoningResult
{
    public string RootCause { get; init; } = "";
    public string PatchJson { get; init; } = "";
    public string SelfConsistency { get; init; } = "";
    public string Explanation { get; init; } = "";
    public IReadOnlyList<LlmExchange> Exchanges { get; init; } = [];
}

public sealed record PatchApplicationResult
{
    public bool Applied { get; init; }
    public string OriginalCode { get; init; } = "";
    public string PatchedCode { get; init; } = "";
    public string UnifiedDiff { get; init; } = "";
    public string TargetMethod { get; init; } = "";
    public IReadOnlyList<string> Messages { get; init; } = [];
    public IReadOnlyList<ChangedLineRange> ChangedRanges { get; init; } = [];
}

public sealed record ChangedLineRange(int OldStartLine, int OldEndLine, int NewStartLine, int NewEndLine);

public sealed record ValidationCheck(string Rule, bool Passed, string Message, object? Data = null);

public sealed record ValidationResult
{
    public bool Passed { get; init; }
    public IReadOnlyList<ValidationCheck> Checks { get; init; } = [];
    public string RefinementInstruction { get; init; } = "";
    public PatchApplicationResult PatchApplication { get; init; } = new();
}

public sealed record RefinementIteration
{
    public int Iteration { get; init; }
    public string PatchJson { get; init; } = "";
    public string SelfConsistency { get; init; } = "";
    public ValidationResult Validation { get; init; } = new();
    public IReadOnlyList<LlmExchange> Exchanges { get; init; } = [];
}

public sealed record PipelineTraceEntry(DateTimeOffset Timestamp, string Stage, string Message);

public sealed record RepairPipelineResult
{
    public IReadOnlyList<CandidateOutcome> Candidates { get; init; } = [];
    public IReadOnlyList<ModelCall> ModelCalls { get; init; } = [];
    public double DurationSeconds { get; init; }
    public bool Accepted { get; init; }
    public bool? FrameworkPlausible { get; init; }
    public string RunId { get; init; } = Guid.NewGuid().ToString("n");
    public RepairRequest Request { get; init; } = new();
    public PreprocessingResult Preprocessing { get; init; } = new();
    public RankingResult Ranking { get; init; } = new();
    public SuspiciousMethodScore? SelectedMethod { get; init; }
    public ReasoningResult Reasoning { get; init; } = new();
    public ValidationResult FinalValidation { get; init; } = new();
    public IReadOnlyList<RefinementIteration> RefinementIterations { get; init; } = [];
    public IReadOnlyList<PipelineTraceEntry> Trace { get; init; } = [];
    public string FinalStatus { get; init; } = "unrepaired";
    public string LogPath { get; init; } = "";
    public string LogDirectory { get; init; } = "";
    public bool UsedRealLlmClient { get; init; }
    public bool MockClientUsed { get; init; }
    public string LlmClientName { get; init; } = "";
    public string ModelName { get; init; } = "";
    public IReadOnlyList<string> PromptTemplatesUsed { get; init; } = [];
    public string ErrorMessage { get; init; } = "";
}

public sealed record EnvironmentDiagnostics
{
    public string OperatingSystem { get; init; } = "";
    public string CpuArchitecture { get; init; } = "";
    public string DotNetVersion { get; init; } = "";
    public IReadOnlyDictionary<string, string> DependencyVersions { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> CompilerPaths { get; init; } = new Dictionary<string, string>();
    public string OpenAiModelName { get; init; } = "";
    public string LlmProvider { get; init; } = "";
    public bool UseMockClient { get; init; }
    public float Temperature { get; init; }
    public int MaxTokens { get; init; }
    public IReadOnlyList<string> TimestampedExperimentLogs { get; init; } = [];
    public IReadOnlyList<string> DatasetReferences { get; init; } = [];
}

public sealed record BenchmarkCase(string CaseId, RepairRequest Request);

public sealed record BenchmarkRunExport
{
    public string CaseId { get; init; } = "";
    public RepairPipelineResult Result { get; init; } = new();
}

public static class JsonLog
{
    public static readonly System.Text.Json.JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
}
