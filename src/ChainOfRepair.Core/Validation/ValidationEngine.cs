using System.Diagnostics;
using System.Text.RegularExpressions;
using ChainOfRepair.Core.Models;

namespace ChainOfRepair.Core.Validation;

public sealed class ValidationService
{
    public async Task<ValidationResult> ValidateAsync(
        RepairRequest request,
        CandidateMethod method,
        string rootCauseJson,
        string patchJson,
        PatchApplicationResult patchApplication,
        CancellationToken cancellationToken = default)
    {
        var checks = new List<ValidationCheck>
        {
            new(
                "Rule 0: Patch Application",
                patchApplication.Applied,
                patchApplication.Applied
                    ? "Unified diff was applied to the original source code."
                    : "Unified diff could not be applied to the original source code: " + string.Join(" ", patchApplication.Messages),
                patchApplication)
        };

        checks.AddRange(await RunSyntaxOrCompileFilterAsync(request, patchApplication.PatchedCode, cancellationToken));
        checks.Add(RunDifferentialStaticAnalysis(request.SourceCode, patchApplication.PatchedCode, patchApplication));
        checks.Add(RunCausalAlignment(method, rootCauseJson, patchJson, patchApplication));

        var passed = checks.All(c => c.Passed);
        return new ValidationResult
        {
            Passed = passed,
            Checks = checks,
            RefinementInstruction = passed ? "" : BuildRefinementInstruction(checks),
            PatchApplication = patchApplication
        };
    }

    private async Task<IReadOnlyList<ValidationCheck>> RunSyntaxOrCompileFilterAsync(RepairRequest request, string patchedSource, CancellationToken cancellationToken)
    {
        var checks = new List<ValidationCheck>();
        var command = GetCompilerCommand(request.Language);
        if (command is null)
        {
            checks.Add(new ValidationCheck(
                "Rule A: Compilation / Syntax Filter",
                FallbackSyntaxCheck(patchedSource),
                $"Compiler unavailable; fallback structural syntax validation used for {request.Language}.",
                new { Mode = "fallback-structural" }));
            return checks;
        }

        try
        {
            using var temp = new TemporarySourceFile(request.Language, patchedSource, request.FileName);
            var args = BuildCompilerArguments(request.Language, temp.Path);
            var result = await RunProcessAsync(command, args, cancellationToken);
            checks.Add(new ValidationCheck(
                "Rule A: Compilation / Syntax Filter",
                result.ExitCode == 0,
                result.ExitCode == 0
                    ? $"Compiler/syntax command succeeded: {command} {args}"
                    : $"Compiler/syntax command failed: {result.Output}",
                new { Mode = "real-tool", Command = command, Arguments = args, result.ExitCode }));
        }
        catch (Exception ex)
        {
            checks.Add(new ValidationCheck(
                "Rule A: Compilation / Syntax Filter",
                FallbackSyntaxCheck(patchedSource),
                $"Compiler wrapper failed; fallback structural syntax validation used. {ex.Message}",
                new { Mode = "fallback-structural" }));
        }

        return checks;
    }

    private static ValidationCheck RunDifferentialStaticAnalysis(string original, string patched, PatchApplicationResult patchApplication)
    {
        var introduced = new List<string>();
        if (string.Equals(original, patched, StringComparison.Ordinal))
        {
            introduced.Add("patch did not change the source code");
        }

        if (CountMatches(patched, @"\breturn\b") < CountMatches(original, @"\breturn\b") && Regex.IsMatch(original, @"\b(return|=>)\b"))
        {
            introduced.Add("possible missing return");
        }

        if (CountMatches(patched, @"\{") != CountMatches(patched, @"\}"))
        {
            introduced.Add("unmatched braces");
        }

        if (patched.Replace("\r\n", "\n").Split('\n').Any(line => Regex.IsMatch(line, @"\breturn\b[^;]*;\s*[^\s}]")))
        {
            introduced.Add("possible unreachable code after return");
        }

        if (Regex.IsMatch(patched, @"\[[^\]]+\+\+|--[^\]]+\]"))
        {
            introduced.Add("suspicious increment/decrement inside index expression");
        }

        if (Regex.IsMatch(patched, @"\.\w+\s*\(") &&
            Regex.IsMatch(patched, @"\b(null|None|NULL)\b") &&
            !Regex.IsMatch(patched, @"\b(if|guard|throw|return)\b.*\b(null|None|NULL)\b"))
        {
            introduced.Add("possible null dereference-like pattern");
        }

        return new ValidationCheck(
            "Rule B: Differential Static Analysis Filter",
            introduced.Count == 0,
            introduced.Count == 0 ? "No new static-differential issues were detected in the patched code." : $"Potential issues introduced: {string.Join(", ", introduced)}.",
            new { patchApplication.Messages, patchApplication.ChangedRanges, Issues = introduced });
    }

    private static ValidationCheck RunCausalAlignment(CandidateMethod method, string rootCauseJson, string patchJson, PatchApplicationResult patchApplication)
    {
        var issues = new List<string>();
        if (patchApplication.ChangedRanges.Count == 0)
        {
            issues.Add("patch did not report any changed line ranges");
        }

        foreach (var range in patchApplication.ChangedRanges)
        {
            if (range.OldStartLine < method.StartLine || range.OldEndLine > method.EndLine)
            {
                issues.Add($"changed range {range.OldStartLine}-{range.OldEndLine} falls outside localized boundary {method.StartLine}-{method.EndLine}");
            }
        }

        var fileHeaders = patchApplication.UnifiedDiff.Split('\n')
            .Where(line => line.StartsWith("--- ", StringComparison.Ordinal) || line.StartsWith("+++ ", StringComparison.Ordinal))
            .ToArray();
        if (fileHeaders.Length > 2)
        {
            issues.Add("patch touches multiple files");
        }

        if (!PatchMentionsRootCauseCategory(rootCauseJson, patchJson, patchApplication.UnifiedDiff))
        {
            issues.Add("patch does not appear related to the root-cause fault category");
        }

        if (Regex.IsMatch(patchApplication.UnifiedDiff, @"^\+.*\b(class|interface|struct|record)\b", RegexOptions.Multiline))
        {
            issues.Add("patch introduces unrelated type-level refactoring");
        }

        return new ValidationCheck(
            "Rule C: Fault-Patch Causal Alignment",
            issues.Count == 0,
            issues.Count == 0 ? "Patch is method-scoped and causally aligned with the inferred fault category." : string.Join("; ", issues),
            new { patchApplication.ChangedRanges, Issues = issues });
    }

    private static bool PatchMentionsRootCauseCategory(string rootCauseJson, string patchJson, string unifiedDiff)
    {
        var rootLower = rootCauseJson.ToLowerInvariant();
        var patchLower = (patchJson + "\n" + unifiedDiff).ToLowerInvariant();
        return (rootLower.Contains("null") && patchLower.Contains("null")) ||
               (rootLower.Contains("bound") && Regex.IsMatch(patchLower, @"(<|>|<=|>=|length|size|count|len|index)")) ||
               (rootLower.Contains("exception") && Regex.IsMatch(patchLower, @"(try|catch|except|throw|raise)")) ||
               (rootLower.Contains("type") && Regex.IsMatch(patchLower, @"(parse|convert|cast|\()")) ||
               (rootLower.Contains("division") && Regex.IsMatch(patchLower, @"(/|zero|0)")) ||
               (!rootLower.Contains("null") &&
                !rootLower.Contains("bound") &&
                !rootLower.Contains("exception") &&
                !rootLower.Contains("type") &&
                !rootLower.Contains("division"));
    }

    private static string? GetCompilerCommand(SupportedLanguage language)
    {
        var env = language switch
        {
            SupportedLanguage.Java => Environment.GetEnvironmentVariable("COR_JAVA_COMPILER") ?? FindOnPath("javac"),
            SupportedLanguage.Python => Environment.GetEnvironmentVariable("COR_PYTHON") ?? FindOnPath("python") ?? FindOnPath("python3"),
            SupportedLanguage.C => Environment.GetEnvironmentVariable("COR_C_COMPILER") ?? FindOnPath("gcc") ?? FindOnPath("clang"),
            SupportedLanguage.CSharp => Environment.GetEnvironmentVariable("COR_CSHARP_BUILD"),
            _ => null
        };

        return string.IsNullOrWhiteSpace(env) ? null : env;
    }

    private static string BuildCompilerArguments(SupportedLanguage language, string sourcePath) => language switch
    {
        SupportedLanguage.Java => $"-Xlint:none \"{sourcePath}\"",
        SupportedLanguage.Python => $"-m py_compile \"{sourcePath}\"",
        SupportedLanguage.C => $"-fsyntax-only \"{sourcePath}\"",
        SupportedLanguage.CSharp => $"\"{sourcePath}\"",
        _ => ""
    };

    private static bool FallbackSyntaxCheck(string source)
    {
        return CountMatches(source, @"\{") == CountMatches(source, @"\}") &&
               CountMatches(source, @"\(") == CountMatches(source, @"\)") &&
               CountMatches(source, @"\[") == CountMatches(source, @"\]");
    }

    private static async Task<(int ExitCode, string Output)> RunProcessAsync(string fileName, string arguments, CancellationToken cancellationToken)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, (await outputTask + await errorTask).Trim());
    }

    private static string? FindOnPath(string command)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var extensions = OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd", ".bat", "" } : new[] { "" };
        foreach (var directory in path.Split(Path.PathSeparator))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory.Trim(), command + extension);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static string BuildRefinementInstruction(IEnumerable<ValidationCheck> checks)
    {
        var failures = checks.Where(c => !c.Passed).Select(c => $"{c.Rule}: {c.Message}");
        return "Validation failed on the patched code. Preserve the original root-cause diagnosis and produce a smaller method-scoped patch. Failures: " + string.Join(" | ", failures);
    }

    private static int CountMatches(string text, string pattern) => Regex.Matches(text, pattern).Count;

    private sealed class TemporarySourceFile : IDisposable
    {
        private readonly string _directory;

        public TemporarySourceFile(SupportedLanguage language, string source, string? requestedFileName)
        {
            _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cor-" + Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(_directory);
            var extension = language switch
            {
                SupportedLanguage.Java => ".java",
                SupportedLanguage.Python => ".py",
                SupportedLanguage.C => ".c",
                SupportedLanguage.CSharp => ".cs",
                _ => ".txt"
            };
            var fileName = string.IsNullOrWhiteSpace(requestedFileName) ? "Input" + extension : System.IO.Path.GetFileName(requestedFileName);
            Path = System.IO.Path.Combine(_directory, fileName);
            File.WriteAllText(Path, source);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch
            {
                // Best-effort cleanup only.
            }
        }
    }
}
