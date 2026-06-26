using System.Text.RegularExpressions;
using ChainOfRepair.Core.Models;

namespace ChainOfRepair.Core.Ranking;

public sealed class WeightedBordaRanker
{
    public RankingResult Rank(RepairRequest request, PreprocessingResult preprocessing)
    {
        var candidates = preprocessing.CandidateMethods.ToArray();
        if (candidates.Length == 0)
        {
            return new RankingResult();
        }

        var weights = NormalizeWeights(
            ArtifactWeights.Defaults,
            hasSource: !string.IsNullOrWhiteSpace(request.SourceCode),
            hasFailingOutput: !string.IsNullOrWhiteSpace(request.FailingOutput),
            hasBugReport: !string.IsNullOrWhiteSpace(request.BugDescription),
            hasRevisionHistory: false);

        var sourceRaw = candidates.ToDictionary(m => m, SourceScore);
        var failingRaw = candidates.ToDictionary(m => m, m => FailingOutputScore(m, preprocessing.FailingOutput));
        var bugRaw = candidates.ToDictionary(m => m, m => BugReportScore(m, request.BugDescription));
        var revisionRaw = candidates.ToDictionary(m => m, _ => 0.0);

        var sourceBorda = ToBorda(sourceRaw);
        var failingBorda = ToBorda(failingRaw);
        var bugBorda = ToBorda(bugRaw);
        var revisionBorda = ToBorda(revisionRaw);

        var ranked = candidates
            .Select(method =>
            {
                var final = weights.SourceContext * sourceBorda[method]
                    + weights.FailingOutput * failingBorda[method]
                    + weights.BugReport * bugBorda[method]
                    + weights.RevisionHistory * revisionBorda[method];

                return new SuspiciousMethodScore
                {
                    Method = method,
                    SourceCodeScore = Round(sourceBorda[method]),
                    StackTraceScore = Round(failingBorda[method]),
                    BugReportScore = Round(bugBorda[method]),
                    RevisionHistoryScore = Round(revisionBorda[method]),
                    FinalSuspiciousnessScore = Round(final),
                    Explanation = Explain(method, preprocessing.FailingOutput, request.BugDescription, sourceRaw[method], failingRaw[method], bugRaw[method])
                };
            })
            .OrderByDescending(x => x.FinalSuspiciousnessScore)
            .ThenBy(x => x.Method.StartLine)
            .Take(Math.Clamp(request.TopK, 1, 10))
            .ToArray();

        return new RankingResult
        {
            NormalizedWeights = weights,
            RankedMethods = ranked
        };
    }

    public ArtifactWeights NormalizeWeights(ArtifactWeights defaults, bool hasSource, bool hasFailingOutput, bool hasBugReport, bool hasRevisionHistory)
    {
        var source = hasSource ? defaults.SourceContext : 0.0;
        var failing = hasFailingOutput ? defaults.FailingOutput : 0.0;
        var bug = hasBugReport ? defaults.BugReport : 0.0;
        var revision = hasRevisionHistory ? defaults.RevisionHistory : 0.0;
        var total = source + failing + bug + revision;

        if (total <= 0)
        {
            return new ArtifactWeights(1, 0, 0, 0);
        }

        return new ArtifactWeights(Round(source / total), Round(failing / total), Round(bug / total), Round(revision / total));
    }

    private static double SourceScore(CandidateMethod method)
    {
        var suspicious = method.SuspiciousConstructs.Count * 1.6;
        var control = method.ControlFlowIndicators.Count * 0.8;
        var size = Math.Min(2.0, Math.Max(0.0, (method.EndLine - method.StartLine + 1) / 80.0));
        return suspicious + control + size;
    }

    private static double FailingOutputScore(CandidateMethod method, FailingOutputAnalysis output)
    {
        var score = 0.0;
        var fullName = $"{method.Context}.{method.Name}";
        if (output.MethodNames.Any(m => m.Equals(method.Name, StringComparison.OrdinalIgnoreCase) || fullName.EndsWith(m, StringComparison.OrdinalIgnoreCase)))
        {
            score += 5.0;
        }

        if (output.LineNumbers.Any(line => line >= method.StartLine && line <= method.EndLine))
        {
            score += 4.0;
        }

        if (output.FileNames.Any(file => method.Context.Contains(Path.GetFileNameWithoutExtension(file), StringComparison.OrdinalIgnoreCase)))
        {
            score += 1.5;
        }

        if (output.ErrorTypes.Count > 0 && method.SuspiciousConstructs.Any(c => c.Contains("exception", StringComparison.OrdinalIgnoreCase) || c.Contains("null", StringComparison.OrdinalIgnoreCase)))
        {
            score += 1.0;
        }

        return score;
    }

    private static double BugReportScore(CandidateMethod method, string? bugReport)
    {
        if (string.IsNullOrWhiteSpace(bugReport))
        {
            return 0.0;
        }

        var tokens = Tokenize(bugReport).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var methodTokens = Tokenize($"{method.Name} {method.Context} {string.Join(' ', method.SuspiciousConstructs)} {string.Join(' ', method.ControlFlowIndicators)}");
        return methodTokens.Count(tokens.Contains);
    }

    private static Dictionary<CandidateMethod, double> ToBorda(Dictionary<CandidateMethod, double> rawScores)
    {
        var count = rawScores.Count;
        if (count == 1)
        {
            return rawScores.ToDictionary(x => x.Key, _ => 1.0);
        }

        var ordered = rawScores
            .OrderByDescending(x => x.Value)
            .ThenBy(x => x.Key.StartLine)
            .Select((item, index) => new { item.Key, item.Value, Rank = index })
            .ToArray();

        var result = new Dictionary<CandidateMethod, double>();
        foreach (var item in ordered)
        {
            result[item.Key] = item.Value <= 0 ? 0.0 : (double)(count - item.Rank - 1) / (count - 1);
        }

        return result;
    }

    private static IEnumerable<string> Tokenize(string text) =>
        Regex.Matches(text, @"[A-Za-z_][A-Za-z0-9_]+").Select(m => m.Value);

    private static string Explain(CandidateMethod method, FailingOutputAnalysis output, string? bugReport, double source, double failing, double bug)
    {
        var reasons = new List<string>();
        if (source > 0) reasons.Add($"source contains {method.SuspiciousConstructs.Count} suspicious constructs and {method.ControlFlowIndicators.Count} control-flow indicators");
        if (failing > 0) reasons.Add("failing output overlaps by method name, file context, line range, or error type");
        if (bug > 0) reasons.Add("bug report vocabulary overlaps with the method/context");
        if (output.LineNumbers.Any(line => line >= method.StartLine && line <= method.EndLine)) reasons.Add("reported failing line falls inside the candidate interval");
        if (string.IsNullOrWhiteSpace(bugReport)) reasons.Add("bug-report artifact was missing and its weight was redistributed");
        return reasons.Count == 0 ? "Ranked by normalized Borda aggregation with no strong artifact-specific signal." : string.Join("; ", reasons) + ".";
    }

    private static double Round(double value) => Math.Round(value, 4, MidpointRounding.AwayFromZero);
}
