using ChainOfRepair.Core.Experiments;
using System.Text;
using System.Text.Json;
using System.Globalization;
using ChainOfRepair.Core.Models;
using ChainOfRepair.Core.Pipeline;
using ChainOfRepair.Core.Ranking;
using ChainOfRepair.Core.Reasoning;
using ChainOfRepair.Core.Validation;

if (args.Contains("--manifest"))
{
    string? Value(string flag) { var i = Array.IndexOf(args, flag); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
    try
    {
        return await ExperimentRunner.RunAsync(Value("--manifest")!, Value("--output") ?? "experiments/results",
            Path.GetFullPath(Value("--prompts") ?? "prompts"), Value("--case"), Value("--configuration"));
    }
    catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 2; }
}
if (args.Length > 0 && args[0] == "report")
{
    if (args.Length != 3) { Console.Error.WriteLine("Usage: report <record-directory> <output-directory>"); return 2; }
    var records = Directory.GetFiles(args[1], "*.record.json", SearchOption.AllDirectories)
        .Select(p => JsonSerializer.Deserialize<ExperimentRecord>(File.ReadAllText(p), JsonLog.Options)!).ToArray();
    if (records.Length == 0) { Console.Error.WriteLine("No measured experiment records found."); return 2; }
    if (records.GroupBy(r => (r.Benchmark, r.BugId, r.Split, r.Configuration, r.Repeat)).Any(g => g.Count() > 1))
    { Console.Error.WriteLine("Duplicate defect/configuration/repeat records; select one batch."); return 2; }
    await ExperimentReports.WriteAsync(records, args[2]); return 0;
}

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

var validationIndex = Array.IndexOf(args, "--validation");
var validationConfiguration = validationIndex >= 0 && validationIndex + 1 < args.Length
    ? JsonSerializer.Deserialize<ValidationConfiguration>(File.ReadAllText(args[validationIndex + 1]), JsonLog.Options) : null;
var pipeline = new RepairPipelineService(
    new CoRReasoningService(llmClient, new PromptTemplateService(promptDirectory)),
    new PatchApplicationService(),
    new ValidationService(validationConfiguration),
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
var failedCases = 0;
foreach (var benchmarkCase in cases)
{
    Console.WriteLine($"Running {benchmarkCase.CaseId}...");
    var result = await pipeline.RunAsync(benchmarkCase.Request);
    if (!result.Accepted) failedCases++;
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
Console.WriteLine($"Completed {cases.Length} case(s): {cases.Length - failedCases} accepted, {failedCases} unsuccessful. Outputs: {options.OutputFolder}");
return failedCases == 0 ? 0 : 1;

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

        Experiments: --manifest <json> --output <folder> [--case <id>] [--configuration <name>]
        Aggregate: report <record-directory> <output-directory>

        Single-file mode:
        Required:
          --input <folder>      Folder containing benchmark instances.
          --output <folder>     Folder where JSON and CSV outputs are written.

        Optional:
          --language <Java|Python|C|CSharp>  Default: Java
          --topk <3|5|10>                    Default: 5
          --validation <json>               Build and differential static-analysis configuration

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
