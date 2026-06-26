using System.Text;
using System.Text.Json;
using System.Globalization;
using ChainOfRepair.Core.Models;
using ChainOfRepair.Core.Pipeline;
using ChainOfRepair.Core.Ranking;
using ChainOfRepair.Core.Reasoning;
using ChainOfRepair.Core.Validation;

var options = CliOptions.Parse(args);
if (options.ShowHelp)
{
    Console.WriteLine(CliOptions.HelpText);
    return 0;
}

Directory.CreateDirectory(options.OutputFolder);
var llmSettings = new LlmSettings
{
    Provider = "OpenAI",
    Model = Environment.GetEnvironmentVariable("COR_OPENAI_MODEL") ?? "gpt-4o",
    Temperature = 0.2f,
    MaxTokens = 2048,
    UseMockClient = false
};
var llmClient = new OpenAiLLMClient(llmSettings);
var promptDirectory = Path.GetFullPath("prompts");

var pipeline = new RepairPipelineService(
    new CoRReasoningService(llmClient, new PromptTemplateService(promptDirectory)),
    new PatchApplicationService(),
    new ValidationService(),
    new WeightedBordaRanker(),
    llmClient,
    Path.Combine(options.OutputFolder, "logs"));

var cases = BenchmarkLoader.Load(options).ToArray();
if (cases.Length == 0)
{
    Console.Error.WriteLine("No benchmark cases found. Expected subfolders or files such as source.java, failing.txt, and bug.txt.");
    return 2;
}

var summaryRows = new List<string> { "case_id,language,top_k,status,selected_method,score,validation_passed,json_path" };
foreach (var benchmarkCase in cases)
{
    Console.WriteLine($"Running {benchmarkCase.CaseId}...");
    var result = await pipeline.RunAsync(benchmarkCase.Request);
    var export = new BenchmarkRunExport { CaseId = benchmarkCase.CaseId, Result = result };
    var jsonPath = Path.Combine(options.OutputFolder, $"{Sanitize(benchmarkCase.CaseId)}.json");
    await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(export, JsonLog.Options));

    var selected = result.SelectedMethod;
    summaryRows.Add(string.Join(',',
        Csv(benchmarkCase.CaseId),
        benchmarkCase.Request.Language,
        benchmarkCase.Request.TopK,
        result.FinalStatus,
        Csv(selected is null ? "" : $"{selected.Method.Context}.{selected.Method.Name}"),
        selected?.FinalSuspiciousnessScore.ToString("0.####", CultureInfo.InvariantCulture) ?? "",
        result.FinalValidation.Passed,
        Csv(jsonPath)));
}

await File.WriteAllLinesAsync(Path.Combine(options.OutputFolder, "summary.csv"), summaryRows);
Console.WriteLine($"Completed {cases.Length} case(s). Outputs: {options.OutputFolder}");
return 0;

static string Csv(object? value)
{
    var text = Convert.ToString(value) ?? "";
    return '"' + text.Replace("\"", "\"\"") + '"';
}

static string Sanitize(string text)
{
    var invalid = Path.GetInvalidFileNameChars().ToHashSet();
    var builder = new StringBuilder(text.Length);
    foreach (var ch in text)
    {
        builder.Append(invalid.Contains(ch) ? '_' : ch);
    }

    return builder.ToString();
}

internal sealed record CliOptions(string InputFolder, string OutputFolder, SupportedLanguage Language, int TopK, bool ShowHelp)
{
    public const string HelpText = """
        ChainOfRepair.Cli

        Required:
          --input <folder>      Folder containing benchmark instances.
          --output <folder>     Folder where JSON and CSV outputs are written.

        Optional:
          --language <Java|Python|C|CSharp>  Default: Java
          --topk <3|5|10>                    Default: 5

        Case layout:
          input/
            case-1/source.java
            case-1/failing.txt
            case-1/bug.txt

        A flat folder with source.*, failing.txt, and bug.txt is also accepted.
        """;

    public static CliOptions Parse(string[] args)
    {
        var input = "";
        var output = "sample_outputs";
        var language = SupportedLanguage.Java;
        var topK = 5;
        var showHelp = args.Length == 0 || args.Contains("--help") || args.Contains("-h");

        for (var i = 0; i < args.Length; i++)
        {
            var current = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : "";
            switch (current)
            {
                case "--input":
                case "-i":
                    input = Next();
                    break;
                case "--output":
                case "-o":
                    output = Next();
                    break;
                case "--language":
                case "-l":
                    Enum.TryParse(Next(), ignoreCase: true, out language);
                    break;
                case "--topk":
                case "-k":
                    int.TryParse(Next(), out topK);
                    break;
            }
        }

        topK = topK is 3 or 5 or 10 ? topK : 5;
        if (string.IsNullOrWhiteSpace(input) && !showHelp)
        {
            showHelp = true;
        }

        return new CliOptions(input, output, language, topK, showHelp);
    }
}

internal static class BenchmarkLoader
{
    public static IEnumerable<BenchmarkCase> Load(CliOptions options)
    {
        if (!Directory.Exists(options.InputFolder))
        {
            yield break;
        }

        if (FindSourceFile(options.InputFolder, options.Language) is { } flatSource)
        {
            yield return LoadCase(options.InputFolder, Path.GetFileName(options.InputFolder), flatSource, options);
            yield break;
        }

        foreach (var directory in Directory.GetDirectories(options.InputFolder).OrderBy(x => x))
        {
            var source = FindSourceFile(directory, options.Language);
            if (source is not null)
            {
                yield return LoadCase(directory, Path.GetFileName(directory), source, options);
            }
        }
    }

    private static BenchmarkCase LoadCase(string directory, string caseId, string sourcePath, CliOptions options)
    {
        var failingPath = Path.Combine(directory, "failing.txt");
        var bugPath = Path.Combine(directory, "bug.txt");
        return new BenchmarkCase(caseId, new RepairRequest
        {
            SourceCode = File.ReadAllText(sourcePath),
            FailingOutput = File.Exists(failingPath) ? File.ReadAllText(failingPath) : "",
            BugDescription = File.Exists(bugPath) ? File.ReadAllText(bugPath) : null,
            Language = options.Language,
            TopK = options.TopK,
            FileName = Path.GetFileName(sourcePath)
        });
    }

    private static string? FindSourceFile(string directory, SupportedLanguage language)
    {
        var extensions = language switch
        {
            SupportedLanguage.Java => new[] { "*.java" },
            SupportedLanguage.Python => new[] { "*.py" },
            SupportedLanguage.C => new[] { "*.c", "*.h" },
            SupportedLanguage.CSharp => new[] { "*.cs" },
            _ => new[] { "*.*" }
        };

        return extensions.SelectMany(pattern => Directory.GetFiles(directory, pattern)).OrderBy(x => x).FirstOrDefault();
    }
}
