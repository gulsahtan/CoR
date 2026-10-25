using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChainOfRepair.Core.Models;
using ChainOfRepair.Core.Parsing;

namespace ChainOfRepair.Core.Validation;

public interface IValidationService
{
    Task<ValidationResult> ValidateAsync(RepairRequest request, CandidateMethod method, string rootCauseJson,
        string patchJson, PatchApplicationResult application, CancellationToken cancellationToken = default);
}

public sealed class ValidationService(ValidationConfiguration? configuration = null) : IValidationService
{
    public async Task<ValidationResult> ValidateAsync(RepairRequest request, CandidateMethod method,
        string rootCauseJson, string patchJson, PatchApplicationResult application, CancellationToken cancellationToken = default)
    {
        var checks = new List<ValidationCheck>();
        var scope = application.Applied && application.ChangedRanges.Count > 0 &&
            application.ChangedRanges.All(r => r.OldStartLine >= method.StartLine && r.OldEndLine <= method.EndLine) &&
            application.UnifiedDiff.Split('\n').Count(l => l.StartsWith("--- ")) <= 1;
        var originalMethods = ArtifactParserFactory.Create(request.Language).Parse(request with { SourceFiles = [] }).CandidateMethods;
        if (originalMethods.Any(m => m.Id != method.Id && m.StartLine <= method.EndLine && m.EndLine >= method.StartLine))
            scope = false; // Overlapping line-level scopes cannot establish a method-only edit safely.
        checks.Add(new("scope", scope, scope ? "Patch applied within the selected method." :
            "Unified diff could not be applied or changes extend beyond the selected method. " + string.Join(" ", application.Messages)));
        if (!scope) return Result();
        var disabled = request.DisabledValidationRules;
        if (disabled.Contains("all")) return Result();
        var parsed = ArtifactParserFactory.Create(request.Language).Parse(request with { SourceCode = application.PatchedCode, SourceFiles = [] });
        var signaturePreserved = parsed.CandidateMethods.Any(m => m.Name == method.Name && m.Signature == method.Signature);
        if (!disabled.Contains("interface")) checks.Add(new("interface", signaturePreserved, "The selected method signature must remain unchanged."));
        if (!disabled.Contains("semantic"))
        {
            var rules = new Dictionary<string, string>
            {
                ["null-safety"] = @"\b(null|None|NULL)\s*(\.|->)",
                ["boundary"] = @"\[\s*-\d+\s*\]",
                ["control-flow"] = @"\bwhile\s*\(\s*(true|1)\s*\)"
            };
            foreach (var (name, pattern) in rules.Where(r => !disabled.Contains(r.Key)))
            {
                var introduced = Regex.Matches(application.PatchedCode, pattern).Count > Regex.Matches(request.SourceCode, pattern).Count;
                checks.Add(new(name, !introduced, "Conservative local pattern check; not a proof of semantic correctness."));
            }
        }
        using var original = new IsolatedProject(configuration?.ProjectDirectory, request, request.SourceCode);
        using var patched = new IsolatedProject(configuration?.ProjectDirectory, request, application.PatchedCode);
        var build = configuration?.Build ?? DefaultBuild(request);
        if (!disabled.Contains("compilation"))
        {
            if (build is null) checks.Add(new("compilation", false, "Compiler/build command is unavailable; not evaluated."));
            else
            {
                var outcome = await CommandRunner.RunAsync(build, patched.DirectoryPath, request.FileName ?? "input", cancellationToken);
                checks.Add(new("compilation", outcome.Passed, outcome.Passed ? "Patched revision compiled." : "Compilation failed or unavailable: " + outcome.Output + " " + outcome.Error, outcome));
            }
            if (checks.Any(c => c.Rule == "compilation" && !c.Passed)) return Result();
        }
        if (!disabled.Contains("static"))
        {
            if (configuration?.StaticAnalysis is null)
                checks.Add(new("static", false, "Static analyzer is not configured; not evaluated. Structural regex checks do not substitute for it."));
            else
            {
                // Bytecode analyzers need both revisions built under identical settings.
                if (build is not null)
                {
                    var baselineBuild = await CommandRunner.RunAsync(build, original.DirectoryPath, request.FileName ?? "input", cancellationToken);
                    checks.Add(new("baseline-compilation", baselineBuild.Passed, "Original revision build for differential analysis.", baselineBuild));
                    if (!baselineBuild.Passed) { checks.Add(new("static", false, "Baseline build failed; differential analysis unavailable.", baselineBuild)); return Result(); }
                }
                var before = await CommandRunner.RunAsync(configuration.StaticAnalysis, original.DirectoryPath, request.FileName ?? "input", cancellationToken);
                var after = await CommandRunner.RunAsync(configuration.StaticAnalysis, patched.DirectoryPath, request.FileName ?? "input", cancellationToken);
                try
                {
                    if (!before.Passed || !after.Passed) throw new InvalidDataException("Analyzer execution failed.");
                    var oldFindings = JsonSerializer.Deserialize<StaticFinding[]>(before.Output, JsonLog.Options) ?? throw new InvalidDataException("Missing baseline findings.");
                    var newFindings = JsonSerializer.Deserialize<StaticFinding[]>(after.Output, JsonLog.Options) ?? throw new InvalidDataException("Missing patched findings.");
                    var introduced = DifferentialFindings(oldFindings, newFindings, configuration.RejectionSeverity)
                        .Where(f => !disabled.Contains(f.Category) && !(disabled.Contains("type-consistency") && f.Category == "type-mismatch")).ToArray();
                    checks.Add(new("static", introduced.Length == 0, introduced.Length == 0 ? "No new or worsened rejecting findings." : "New or worsened static findings.",
                        new { before, after, oldFindings, newFindings, introduced }));
                }
                catch (Exception ex) when (ex is JsonException or InvalidDataException)
                { checks.Add(new("static", false, ex.Message, new { before, after })); }
            }
        }
        return Result();
        ValidationResult Result() => new() { Passed = checks.All(c => c.Passed), Checks = checks, PatchApplication = application,
            RefinementInstruction = "Validation failed: " + string.Join(" | ", checks.Where(c => !c.Passed).Select(c => c.Rule + ": " + c.Message)) };
    }

    public static StaticFinding[] DifferentialFindings(StaticFinding[] before, StaticFinding[] after, int threshold)
    {
        static string Key(StaticFinding f) => $"{f.Tool}|{f.CheckId}|{f.File.Replace('\\', '/')}|{f.Method}|{f.Category}";
        var old = before.GroupBy(Key).ToDictionary(g => g.Key, g => g.Select(f => f.Severity).OrderDescending().ToArray());
        return after.GroupBy(Key).SelectMany(g => g.OrderByDescending(f => f.Severity).Select((f, i) => (f, i)))
            .Where(x => x.f.Severity >= threshold && (!old.TryGetValue(Key(x.f), out var prior) || x.i >= prior.Length || x.f.Severity > prior[x.i]))
            .Select(x => x.f).ToArray();
    }
    private static CommandSpec? DefaultBuild(RepairRequest request) => request.Language switch
    {
        SupportedLanguage.Java => new() { Executable = Environment.GetEnvironmentVariable("COR_JAVA_COMPILER") ?? "javac", Arguments = ["{file}"] },
        SupportedLanguage.C => new() { Executable = Environment.GetEnvironmentVariable("COR_C_COMPILER") ?? "clang", Arguments = ["-fsyntax-only", "{file}"] },
        SupportedLanguage.Python => new() { Executable = Environment.GetEnvironmentVariable("COR_PYTHON") ?? "python", Arguments = ["-m", "py_compile", "{file}"] },
        SupportedLanguage.CSharp when !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("COR_CSHARP_BUILD")) => new() { Executable = Environment.GetEnvironmentVariable("COR_CSHARP_BUILD")!, Arguments = [] },
        _ => null
    };
}

public static class CommandRunner
{
    public static async Task<CommandOutcome> RunAsync(CommandSpec command, string directory, string file, CancellationToken cancellationToken = default)
    {
        var timer = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(command.TimeoutSeconds, 1, 3600)));
        using var process = new Process { StartInfo = new() { FileName = command.Executable, WorkingDirectory = directory,
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true } };
        foreach (var argument in command.Arguments) process.StartInfo.ArgumentList.Add(argument.Replace("{file}", file).Replace("{project}", directory));
        process.StartInfo.Environment.Remove("OPENAI_API_KEY");
        try
        {
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(timeout.Token);
            return new(process.ExitCode, Redact(await output), timer.Elapsed.TotalSeconds, process.ExitCode == 0 ? null : Redact(await error));
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (cancellationToken.IsCancellationRequested) throw;
            return new(null, "", timer.Elapsed.TotalSeconds, ex is OperationCanceledException ? "Command timed out." : Redact(ex.Message));
        }
    }
    private static string Redact(string text)
    {
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        return string.IsNullOrEmpty(key) ? text : text.Replace(key, "[REDACTED]");
    }
}

public sealed class IsolatedProject : IDisposable
{
    public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "cor-project-" + Guid.NewGuid().ToString("n"));
    public IsolatedProject(string? originalDirectory, RepairRequest request, string source)
    {
        Directory.CreateDirectory(DirectoryPath);
        if (originalDirectory is not null) Copy(originalDirectory, DirectoryPath);
        foreach (var artifact in request.SourceFiles) Write(artifact.Path, artifact.Content);
        Write(request.FileName ?? "input", source);
    }
    public static string SafePath(string root, string relative)
    {
        if (Path.IsPathRooted(relative)) throw new InvalidDataException("Project paths must be relative.");
        var path = Path.GetFullPath(Path.Combine(root, relative));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, comparison)) throw new InvalidDataException("Path escapes project root.");
        return path;
    }
    private void Write(string relative, string content)
    { var path = SafePath(DirectoryPath, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, content); }
    private static void Copy(string source, string target)
    {
        foreach (var item in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Project symlinks/reparse points must be removed before staging.");
            if (item.Name is ".git" or "bin" or "obj" or "__pycache__" or ".vs") continue;
            var destination = Path.Combine(target, item.Name);
            if (item is DirectoryInfo) { Directory.CreateDirectory(destination); Copy(item.FullName, destination); }
            else File.Copy(item.FullName, destination);
        }
    }
    public void Dispose() { try { Directory.Delete(DirectoryPath, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}
