using System.Text.RegularExpressions;
using ChainOfRepair.Core.Models;
using ChainOfRepair.Core.Parsing;

namespace ChainOfRepair.Core.Ranking;

public sealed class WeightedBordaRanker
{
    public RankingResult Rank(RepairRequest request, PreprocessingResult preprocessing)
    {
        var methods = preprocessing.CandidateMethods.ToArray();
        if (methods.Length == 0) return new();
        var weights = NormalizeWeights(request.Weights, true,
            preprocessing.FailingOutput.StackTraceFrames.Count > 0,
            !string.IsNullOrWhiteSpace(request.BugDescription),
            request.RevisionHistory is not null && request.DefectTime is not null);
        var source = Borda(methods.Select(SourceScore).ToArray());
        var trace = Borda(methods.Select(m => TraceScore(m, methods, preprocessing.FailingOutput.StackTraceFrames)).ToArray());
        var report = Borda(LexicalScores(methods, request.BugDescription));
        var history = Borda(methods.Select(m => HistoryScore(m, request)).ToArray());
        var ranked = methods.Select((method, i) => new SuspiciousMethodScore
        {
            Method = method, SourceCodeScore = source[i], StackTraceScore = trace[i],
            BugReportScore = report[i], RevisionHistoryScore = history[i],
            FinalSuspiciousnessScore = weights.SourceContext * source[i] + weights.FailingOutput * trace[i]
                + weights.BugReport * report[i] + weights.RevisionHistory * history[i],
            Explanation = "Average-tie Borda ranks; available artifact weights renormalized. See docs/ranking.md for raw scoring rules."
        }).OrderByDescending(m => m.FinalSuspiciousnessScore).ThenByDescending(m => weights.SourceContext > 0 ? m.SourceCodeScore : 0)
          .ThenBy(m => m.Method.Id, StringComparer.Ordinal).ToArray();
        // Keep the full ordering for retrospective Top-5 even when the search uses Top-k=3.
        return new() { NormalizedWeights = weights, RankedMethods = ranked };
    }

    public ArtifactWeights NormalizeWeights(ArtifactWeights w, bool hasSource, bool hasFailingOutput, bool hasBugReport, bool hasRevisionHistory)
    {
        var values = new[] { w.SourceContext, w.FailingOutput, w.BugReport, w.RevisionHistory };
        if (values.Any(v => !double.IsFinite(v) || v < 0)) throw new ArgumentException("Weights must be finite and nonnegative.");
        var a = hasSource ? w.SourceContext : 0;
        var b = hasFailingOutput ? w.FailingOutput : 0;
        var c = hasBugReport ? w.BugReport : 0;
        var d = hasRevisionHistory ? w.RevisionHistory : 0;
        var total = a + b + c + d;
        return total == 0 ? new(0, 0, 0, 0) : new(a / total, b / total, c / total, d / total);
    }

    public static double[] Borda(double[] raw)
    {
        if (raw.Length == 1) return [1];
        var result = new double[raw.Length];
        var occupied = 0;
        foreach (var group in raw.Select((score, index) => (score, index)).GroupBy(x => x.score).OrderByDescending(g => g.Key))
        {
            var rank = occupied + (group.Count() + 1) / 2.0;
            foreach (var item in group) result[item.index] = (raw.Length - rank) / (raw.Length - 1);
            occupied += group.Count();
        }
        return result;
    }

    public static double SourceScore(CandidateMethod m)
    {
        var patterns = new[] { @"<=|>=|\b(length|size|len|count)\b", @"\b\w+\s*(\.|->)\s*\w+",
            @"\b\w+\s*\[[^\]]+\]", @"\b(throw|throws|try|catch|except|raise|finally)\b",
            @"&&|\|\||\b(and|or)\b|==|!=" };
        var code = SourceLexing.Mask(m.Source, m.Signature.StartsWith("def ", StringComparison.Ordinal));
        return patterns.Count(p => Regex.IsMatch(code, p)) / 5.0;
    }

    private static double TraceScore(CandidateMethod method, CandidateMethod[] methods, IReadOnlyList<StackTraceFrameInfo> frames)
    {
        for (var j = 0; j < frames.Count; j++)
        {
            var frame = frames[j];
            var matches = methods.Where(m =>
                (frame.FileName is null || Path.GetFileName(m.FilePath) == Path.GetFileName(frame.FileName)) &&
                (frame.MethodName == m.Name || frame.MethodName == $"{m.Context}.{m.Name}" ||
                    frame.MethodName?.EndsWith($".{m.Context}.{m.Name}", StringComparison.Ordinal) == true) &&
                (frame.LineNumber is null || frame.LineNumber >= m.StartLine && frame.LineNumber <= m.EndLine)).ToArray();
            if (matches.Length == 1 && matches[0] == method) return 1.0 / (j + 1);
        }
        return 0;
    }

    private static double HistoryScore(CandidateMethod method, RepairRequest request)
    {
        if (request.DefectTime is null || request.RevisionHistory is null) return 0;
        if (request.HistoryWindowDays <= 0 || !double.IsFinite(request.HistoryDecayPerDay) || request.HistoryDecayPerDay < 0)
            throw new ArgumentException("Invalid history window or decay.");
        return request.RevisionHistory.Where(h => h.MethodId == method.Id)
            .DistinctBy(h => h.Commit).Select(h => (request.DefectTime.Value - h.Timestamp).TotalDays)
            .Where(days => days > 0 && days <= request.HistoryWindowDays)
            .Sum(days => Math.Exp(-request.HistoryDecayPerDay * days));
    }

    public static double[] LexicalScores(CandidateMethod[] methods, string? report)
    {
        var documents = methods.Select(m => Tokens($"{m.Context} {m.Signature} {string.Join(' ', Regex.Matches(m.Source, @"//[^\n]*|/\*[\s\S]*?\*/|#[^\n]*").Select(x => x.Value))}").ToArray()).ToArray();
        var vocabulary = documents.SelectMany(d => d).Distinct(StringComparer.Ordinal).ToArray();
        var query = Tokens(report ?? "").ToArray();
        var idf = vocabulary.ToDictionary(t => t, t => Math.Log((1.0 + methods.Length) / (1 + documents.Count(d => d.Contains(t)))) + 1);
        Dictionary<string, double> Vector(string[] words) => words.Where(idf.ContainsKey).GroupBy(t => t)
            .ToDictionary(g => g.Key, g => g.Count() * idf[g.Key]);
        var q = Vector(query);
        var qNorm = Math.Sqrt(q.Values.Sum(v => v * v));
        return documents.Select(d =>
        {
            var v = Vector(d); var norm = Math.Sqrt(v.Values.Sum(x => x * x));
            return qNorm == 0 || norm == 0 ? 0 : v.Sum(x => x.Value * q.GetValueOrDefault(x.Key)) / (qNorm * norm);
        }).ToArray();
    }
    private static IEnumerable<string> Tokens(string text) => Regex.Matches(
        Regex.Replace(Regex.Replace(text, "([A-Z]+)([A-Z][a-z])", "$1 $2"), "([a-z0-9])([A-Z])", "$1 $2").Replace('_', ' ').ToLowerInvariant(),
        @"[a-z][a-z0-9]*").Select(m => m.Value);
}
