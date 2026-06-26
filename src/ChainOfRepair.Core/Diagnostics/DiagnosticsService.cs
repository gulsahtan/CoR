using ChainOfRepair.Core.Models;

namespace ChainOfRepair.Core.Diagnostics;

public sealed class DiagnosticsService
{
    private readonly LlmSettings _llmSettings;
    private readonly string _logDirectory;

    public DiagnosticsService(LlmSettings llmSettings, string? logDirectory = null)
    {
        _llmSettings = llmSettings;
        _logDirectory = logDirectory ?? Path.Combine(AppContext.BaseDirectory, "logs");
    }

    public EnvironmentDiagnostics GetDiagnostics()
    {
        var dependencyVersions = new Dictionary<string, string>
        {
            ["ChainOfRepair.Core"] = typeof(DiagnosticsService).Assembly.GetName().Version?.ToString() ?? "local",
            ["OpenAI API client"] = "HTTP Chat Completions"
        };

        return new EnvironmentDiagnostics
        {
            OperatingSystem = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            CpuArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            DotNetVersion = Environment.Version.ToString(),
            DependencyVersions = dependencyVersions,
            CompilerPaths = new Dictionary<string, string>
            {
                ["Java"] = Environment.GetEnvironmentVariable("COR_JAVA_COMPILER") ?? "PATH: javac",
                ["Python"] = Environment.GetEnvironmentVariable("COR_PYTHON") ?? "PATH: python/python3",
                ["C"] = Environment.GetEnvironmentVariable("COR_C_COMPILER") ?? "PATH: gcc/clang",
                ["CSharp"] = Environment.GetEnvironmentVariable("COR_CSHARP_BUILD") ?? "fallback structural syntax"
            },
            OpenAiModelName = _llmSettings.Model,
            LlmProvider = _llmSettings.Provider,
            UseMockClient = _llmSettings.UseMockClient,
            Temperature = _llmSettings.Temperature,
            MaxTokens = _llmSettings.MaxTokens,
            TimestampedExperimentLogs = Directory.Exists(_logDirectory)
                ? Directory.GetFiles(_logDirectory, "final_result.json", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).Take(50).ToArray()
                : [],
            DatasetReferences =
            [
                "Defects4J: https://github.com/rjust/defects4j",
                "QuixBugs: https://github.com/jkoppel/QuixBugs",
                "IntroClass: https://github.com/ProgramRepair/IntroClass"
            ]
        };
    }
}
