using System.Text.RegularExpressions;
using ChainOfRepair.Core.Models;
using ChainOfRepair.Core.Reasoning;

namespace ChainOfRepair.Core.Validation;

public sealed class PatchApplicationService
{
    private static readonly Regex HunkHeader = new(
        @"@@\s+-(?<oldStart>\d+)(?:,(?<oldCount>\d+))?\s+\+(?<newStart>\d+)(?:,(?<newCount>\d+))?\s+@@",
        RegexOptions.Compiled);

    public PatchApplicationResult Apply(RepairRequest request, CandidateMethod localizedMethod, string patchJson)
    {
        var messages = new List<string>();
        string unifiedDiff;
        string targetMethod;

        try
        {
            unifiedDiff = JsonResponse.GetRequiredString(patchJson, "unifiedDiff");
            targetMethod = TryGetString(patchJson, "targetMethod") ?? localizedMethod.Name;
        }
        catch (Exception ex)
        {
            return Failure(request.SourceCode, "", localizedMethod.Name, $"Patch response was not valid patch JSON: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(unifiedDiff))
        {
            return Failure(request.SourceCode, unifiedDiff, targetMethod, "Patch JSON contained an empty unifiedDiff.");
        }

        var originalLines = NormalizeLines(request.SourceCode);
        var working = originalLines.ToList();
        var changedRanges = new List<ChangedLineRange>();
        var diffLines = NormalizeLines(unifiedDiff);
        var offset = 0;
        var hunkCount = 0;

        for (var i = 0; i < diffLines.Length; i++)
        {
            var match = HunkHeader.Match(diffLines[i]);
            if (!match.Success)
            {
                continue;
            }

            hunkCount++;
            var oldStart = int.Parse(match.Groups["oldStart"].Value);
            var oldIndex = Math.Max(0, oldStart - 1 + offset);
            var cursor = oldIndex;
            var replacement = new List<string>();
            var oldTouched = new List<int>();
            var newTouched = new List<int>();

            i++;
            while (i < diffLines.Length && !diffLines[i].StartsWith("@@", StringComparison.Ordinal))
            {
                var line = diffLines[i];
                if (line.StartsWith("--- ", StringComparison.Ordinal) || line.StartsWith("+++ ", StringComparison.Ordinal))
                {
                    i++;
                    continue;
                }

                if (line.Length == 0)
                {
                    replacement.Add("");
                    cursor++;
                    i++;
                    continue;
                }

                var marker = line[0];
                var content = line.Length > 1 ? line[1..] : "";
                switch (marker)
                {
                    case ' ':
                        if (!LineMatches(working, cursor, content))
                        {
                            return Failure(request.SourceCode, unifiedDiff, targetMethod, $"Unified diff context mismatch at source line {cursor + 1}.");
                        }

                        replacement.Add(working[cursor]);
                        cursor++;
                        break;
                    case '-':
                        if (!LineMatches(working, cursor, content))
                        {
                            return Failure(request.SourceCode, unifiedDiff, targetMethod, $"Unified diff deletion mismatch at source line {cursor + 1}.");
                        }

                        oldTouched.Add(cursor + 1 - offset);
                        cursor++;
                        break;
                    case '+':
                        replacement.Add(content);
                        newTouched.Add(oldStart + replacement.Count - 1);
                        break;
                    case '\\':
                        break;
                    default:
                        return Failure(request.SourceCode, unifiedDiff, targetMethod, $"Unsupported unified diff line marker '{marker}'.");
                }

                i++;
            }

            i--;
            var removeCount = cursor - oldIndex;
            if (oldIndex > working.Count)
            {
                return Failure(request.SourceCode, unifiedDiff, targetMethod, $"Unified diff hunk starts beyond end of source at line {oldStart}.");
            }

            working.RemoveRange(oldIndex, Math.Min(removeCount, working.Count - oldIndex));
            working.InsertRange(oldIndex, replacement);
            offset += replacement.Count - removeCount;

            if (oldTouched.Count > 0 || newTouched.Count > 0)
            {
                var oldStartLine = oldTouched.Count == 0 ? oldStart : oldTouched.Min();
                var oldEndLine = oldTouched.Count == 0 ? oldStart : oldTouched.Max();
                var newStartLine = newTouched.Count == 0 ? oldStartLine : newTouched.Min();
                var newEndLine = newTouched.Count == 0 ? newStartLine : newTouched.Max();
                changedRanges.Add(new ChangedLineRange(oldStartLine, oldEndLine, newStartLine, newEndLine));
            }
        }

        if (hunkCount == 0)
        {
            return Failure(request.SourceCode, unifiedDiff, targetMethod, "Unified diff did not contain any standard @@ hunks.");
        }

        var patched = string.Join(Environment.NewLine, working);
        messages.Add($"Applied {hunkCount} unified diff hunk(s) to the original source code.");

        return new PatchApplicationResult
        {
            Applied = true,
            OriginalCode = request.SourceCode,
            PatchedCode = patched,
            UnifiedDiff = unifiedDiff,
            TargetMethod = targetMethod,
            Messages = messages,
            ChangedRanges = changedRanges
        };
    }

    private static PatchApplicationResult Failure(string original, string unifiedDiff, string targetMethod, string message) =>
        new()
        {
            Applied = false,
            OriginalCode = original,
            PatchedCode = original,
            UnifiedDiff = unifiedDiff,
            TargetMethod = targetMethod,
            Messages = [message]
        };

    private static bool LineMatches(IReadOnlyList<string> lines, int index, string expected) =>
        index >= 0 && index < lines.Count && string.Equals(lines[index], expected, StringComparison.Ordinal);

    private static string[] NormalizeLines(string source) =>
        source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    private static string? TryGetString(string json, string propertyName)
    {
        try
        {
            return JsonResponse.GetRequiredString(json, propertyName);
        }
        catch
        {
            return null;
        }
    }
}
