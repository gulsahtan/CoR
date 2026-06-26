using System.Text.RegularExpressions;
using ChainOfRepair.Core.Models;

namespace ChainOfRepair.Core.Parsing;

public interface IArtifactParser
{
    SupportedLanguage Language { get; }
    PreprocessingResult Parse(RepairRequest request);
}

public sealed class JavaArtifactParser : RegexArtifactParser
{
    public JavaArtifactParser() : base(
        SupportedLanguage.Java,
        new Regex(@"(?<prefix>(public|private|protected|static|final|synchronized|\s)+)\s*(?<return>[\w<>\[\], ?]+)\s+(?<name>[A-Za-z_]\w*)\s*\([^;{}]*\)\s*(throws\s+[\w,\s]+)?\{", RegexOptions.Compiled),
        classRegex: new Regex(@"\b(class|interface|enum)\s+(?<name>[A-Za-z_]\w*)", RegexOptions.Compiled))
    {
    }
}

public sealed class PythonArtifactParser : RegexArtifactParser
{
    public PythonArtifactParser() : base(
        SupportedLanguage.Python,
        new Regex(@"^\s*def\s+(?<name>[A-Za-z_]\w*)\s*\([^)]*\)\s*:", RegexOptions.Compiled | RegexOptions.Multiline),
        classRegex: new Regex(@"^\s*class\s+(?<name>[A-Za-z_]\w*)", RegexOptions.Compiled | RegexOptions.Multiline))
    {
    }
}

public sealed class CArtifactParser : RegexArtifactParser
{
    public CArtifactParser() : base(
        SupportedLanguage.C,
        new Regex(@"(?<return>\b(?:static\s+)?(?:void|int|char|float|double|long|short|size_t|bool|struct\s+\w+|[\w]+\s*\*)[\w\s\*]*?)\s+(?<name>[A-Za-z_]\w*)\s*\([^;{}]*\)\s*\{", RegexOptions.Compiled),
        classRegex: null)
    {
    }
}

public sealed class CSharpArtifactParser : RegexArtifactParser
{
    public CSharpArtifactParser() : base(
        SupportedLanguage.CSharp,
        new Regex(@"(?<prefix>(public|private|protected|internal|static|async|virtual|override|sealed|partial|\s)+)\s*(?<return>[\w<>\[\], ?]+)\s+(?<name>[A-Za-z_]\w*)\s*\([^;{}]*\)\s*\{", RegexOptions.Compiled),
        classRegex: new Regex(@"\b(class|struct|record|interface)\s+(?<name>[A-Za-z_]\w*)", RegexOptions.Compiled))
    {
    }
}

public static class ArtifactParserFactory
{
    public static IArtifactParser Create(SupportedLanguage language) => language switch
    {
        SupportedLanguage.Java => new JavaArtifactParser(),
        SupportedLanguage.Python => new PythonArtifactParser(),
        SupportedLanguage.C => new CArtifactParser(),
        SupportedLanguage.CSharp => new CSharpArtifactParser(),
        _ => new JavaArtifactParser()
    };
}

public abstract class RegexArtifactParser : IArtifactParser
{
    private readonly Regex _methodRegex;
    private readonly Regex? _classRegex;

    protected RegexArtifactParser(SupportedLanguage language, Regex methodRegex, Regex? classRegex)
    {
        Language = language;
        _methodRegex = methodRegex;
        _classRegex = classRegex;
    }

    public SupportedLanguage Language { get; }

    public PreprocessingResult Parse(RepairRequest request)
    {
        var logs = new List<PreprocessingLogEntry>();
        logs.Add(Log("input.accepted", $"Accepted {Language} source text.", new { request.TopK, request.FileName }));

        var lines = NormalizeLines(request.SourceCode);
        logs.Add(Log("source.normalized", "Normalized source into line-indexed representation.", new { LineCount = lines.Length }));

        var methods = ExtractMethods(request.SourceCode, lines, request.FileName, logs);
        logs.Add(Log("source.methods.extracted", "Extracted candidate method/function regions.", new { Count = methods.Count }));

        var failingOutput = FailingOutputParser.Parse(request.FailingOutput);
        logs.Add(Log("failing-output.parsed", "Extracted stack traces, compiler diagnostics, files, methods, lines, and error types.", failingOutput));

        return new PreprocessingResult
        {
            CandidateMethods = methods,
            FailingOutput = failingOutput,
            Logs = logs
        };
    }

    private List<CandidateMethod> ExtractMethods(string source, string[] lines, string? fileName, List<PreprocessingLogEntry> logs)
    {
        var methods = new List<CandidateMethod>();
        foreach (Match match in _methodRegex.Matches(source))
        {
            var name = match.Groups["name"].Value;
            var startLine = GetLineNumber(source, match.Index);
            var endLine = Language == SupportedLanguage.Python
                ? FindPythonFunctionEnd(lines, startLine)
                : FindBraceRegionEnd(lines, startLine);
            var context = FindContext(source, match.Index, fileName);
            var methodLines = lines.Skip(startLine - 1).Take(Math.Max(1, endLine - startLine + 1)).ToArray();
            var localLines = lines.Skip(Math.Max(0, startLine - 4)).Take(Math.Min(lines.Length, endLine - startLine + 7)).ToArray();

            var method = new CandidateMethod
            {
                Name = name,
                Context = context,
                StartLine = startLine,
                EndLine = endLine,
                Source = string.Join(Environment.NewLine, methodLines),
                LocalContext = string.Join(Environment.NewLine, localLines),
                ControlFlowIndicators = DetectControlFlow(methodLines),
                SuspiciousConstructs = DetectSuspiciousConstructs(methodLines)
            };

            methods.Add(method);
            logs.Add(Log("source.method.recorded", $"Recorded candidate {name}.", new
            {
                method.Context,
                method.StartLine,
                method.EndLine,
                method.ControlFlowIndicators,
                method.SuspiciousConstructs
            }));
        }

        if (methods.Count == 0 && !string.IsNullOrWhiteSpace(source))
        {
            methods.Add(new CandidateMethod
            {
                Name = "file_scope",
                Context = fileName ?? "input",
                StartLine = 1,
                EndLine = Math.Max(1, lines.Length),
                Source = source,
                LocalContext = source,
                ControlFlowIndicators = DetectControlFlow(lines),
                SuspiciousConstructs = DetectSuspiciousConstructs(lines)
            });
            logs.Add(Log("source.method.fallback", "No function signature was detected; using a file-scope candidate.", null));
        }

        return methods;
    }

    private string FindContext(string source, int index, string? fileName)
    {
        if (_classRegex is null)
        {
            return fileName ?? "file";
        }

        var context = _classRegex.Matches(source[..index]).Cast<Match>().LastOrDefault()?.Groups["name"].Value;
        return string.IsNullOrWhiteSpace(context) ? fileName ?? "file" : context;
    }

    private static IReadOnlyList<string> DetectControlFlow(IEnumerable<string> lines)
    {
        var joined = string.Join('\n', lines);
        var indicators = new List<string>();
        foreach (var token in new[] { "if", "else", "for", "while", "switch", "case", "try", "catch", "finally", "return", "break", "continue", "throw" })
        {
            if (Regex.IsMatch(joined, $@"\b{Regex.Escape(token)}\b"))
            {
                indicators.Add(token);
            }
        }

        return indicators;
    }

    private static IReadOnlyList<string> DetectSuspiciousConstructs(IEnumerable<string> lines)
    {
        var joined = string.Join('\n', lines);
        var constructs = new List<string>();
        if (Regex.IsMatch(joined, @"\b(null|None|NULL|nullptr)\b|==\s*0|!\s*\w+")) constructs.Add("null check");
        if (Regex.IsMatch(joined, @"(<|>|<=|>=|==|!=).*(length|size|count|len)|\[[^\]]+\]")) constructs.Add("boundary condition");
        if (Regex.IsMatch(joined, @"\b(try|catch|except|finally|throw|raise)\b")) constructs.Add("exception handling");
        if (Regex.IsMatch(joined, @"\([A-Za-z_][\w\s\*<>]+\)\s*\w+|\b(parse|Parse|Convert|cast|static_cast)\b")) constructs.Add("type conversion");
        if (Regex.IsMatch(joined, @"\b[A-Za-z_]\w*\s*\(")) constructs.Add("API call");
        if (Regex.IsMatch(joined, @"\bmalloc\b|\bfree\b|\bdelete\b|\bnew\b|\bDispose\b")) constructs.Add("memory/resource management");
        return constructs.Distinct().ToArray();
    }

    private static string[] NormalizeLines(string source) => source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    private static int GetLineNumber(string source, int index)
    {
        var count = 1;
        for (var i = 0; i < index && i < source.Length; i++)
        {
            if (source[i] == '\n')
            {
                count++;
            }
        }

        return count;
    }

    private static int FindBraceRegionEnd(string[] lines, int startLine)
    {
        var depth = 0;
        var started = false;
        for (var i = startLine - 1; i < lines.Length; i++)
        {
            foreach (var ch in lines[i])
            {
                if (ch == '{')
                {
                    depth++;
                    started = true;
                }
                else if (ch == '}')
                {
                    depth--;
                    if (started && depth <= 0)
                    {
                        return i + 1;
                    }
                }
            }
        }

        return lines.Length;
    }

    private static int FindPythonFunctionEnd(string[] lines, int startLine)
    {
        if (startLine < 1 || startLine > lines.Length)
        {
            return lines.Length;
        }

        var signature = lines[startLine - 1];
        var baseIndent = signature.TakeWhile(char.IsWhiteSpace).Count();
        for (var i = startLine; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var indent = line.TakeWhile(char.IsWhiteSpace).Count();
            if (indent <= baseIndent && !line.TrimStart().StartsWith("#", StringComparison.Ordinal))
            {
                return i;
            }
        }

        return lines.Length;
    }

    private static PreprocessingLogEntry Log(string step, string message, object? data) => new(DateTimeOffset.UtcNow, step, message, data);
}

public static class FailingOutputParser
{
    private static readonly Regex PythonFrame = new(@"File ""(?<file>[^""]+)"", line (?<line>\d+), in (?<method>[\w<>]+)", RegexOptions.Compiled);
    private static readonly Regex JavaFrame = new(@"\bat\s+(?<method>[\w.$<>]+)\((?<file>[^:()]+)?(?::(?<line>\d+))?\)", RegexOptions.Compiled);
    private static readonly Regex CSharpFrame = new(@"\bat\s+(?<method>[\w.`<>+]+).*?:line\s+(?<line>\d+)", RegexOptions.Compiled);
    private static readonly Regex Diagnostic = new(@"(?<file>[\w./\\:-]+)?(?:\((?<line>\d+)(?:,\d+)?\)|:(?<line2>\d+))?.*?\b(?<kind>error|warning)\b\s*(?<code>[A-Z]{1,4}\d{3,5}|C\d{4})?:?\s*(?<message>.+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ExceptionType = new(@"\b(?<type>[A-Za-z_][\w.]*?(Exception|Error|Fault|Failure|panic|segmentation fault))\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static FailingOutputAnalysis Parse(string failingOutput)
    {
        var frames = new List<StackTraceFrameInfo>();
        var errors = new List<CompilerDiagnostic>();
        var warnings = new List<CompilerDiagnostic>();
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var methods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lines = new HashSet<int>();
        var errorTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in failingOutput.Replace("\r\n", "\n").Split('\n'))
        {
            AddFrame(PythonFrame.Match(raw), raw);
            AddFrame(JavaFrame.Match(raw), raw);
            AddFrame(CSharpFrame.Match(raw), raw);

            var diagnostic = Diagnostic.Match(raw);
            if (diagnostic.Success)
            {
                var line = ParseInt(diagnostic.Groups["line"].Value) ?? ParseInt(diagnostic.Groups["line2"].Value);
                var file = EmptyToNull(diagnostic.Groups["file"].Value);
                var item = new CompilerDiagnostic(
                    diagnostic.Groups["kind"].Value.ToLowerInvariant(),
                    file,
                    line,
                    EmptyToNull(diagnostic.Groups["code"].Value),
                    diagnostic.Groups["message"].Value.Trim(),
                    raw.Trim());
                if (item.Kind.Equals("warning", StringComparison.OrdinalIgnoreCase))
                {
                    warnings.Add(item);
                }
                else
                {
                    errors.Add(item);
                }

                if (file is not null) files.Add(file);
                if (line is not null) lines.Add(line.Value);
            }

            foreach (Match match in ExceptionType.Matches(raw))
            {
                errorTypes.Add(match.Groups["type"].Value.Trim());
            }
        }

        return new FailingOutputAnalysis
        {
            StackTraceFrames = frames,
            CompilerErrors = errors,
            Warnings = warnings,
            FileNames = files.ToArray(),
            MethodNames = methods.ToArray(),
            LineNumbers = lines.OrderBy(x => x).ToArray(),
            ErrorTypes = errorTypes.ToArray()
        };

        void AddFrame(Match match, string raw)
        {
            if (!match.Success)
            {
                return;
            }

            var file = EmptyToNull(match.Groups["file"].Value);
            var method = EmptyToNull(match.Groups["method"].Value);
            var line = ParseInt(match.Groups["line"].Value);
            frames.Add(new StackTraceFrameInfo(file, method, line, raw.Trim()));
            if (file is not null) files.Add(file);
            if (method is not null) methods.Add(method.Split('.').Last());
            if (line is not null) lines.Add(line.Value);
        }
    }

    private static int? ParseInt(string text) => int.TryParse(text, out var value) ? value : null;

    private static string? EmptyToNull(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
