using System.Globalization;
using System.Text.Json;
using ChainOfRepair.Core.Models;

namespace ChainOfRepair.Core.Experiments;

public static class ExperimentReports
{
    public static async Task WriteAsync(IReadOnlyList<ExperimentRecord> records, string output, CancellationToken ct = default)
    {
        Directory.CreateDirectory(output);
        await ResearchReports.WriteAsync(records, output, ct);
        var rows = new List<string> { "run_id,benchmark,bug_id,revision,split,configuration,repeat,top1,top5,accepted,framework_plausible,correct_repair,patches,refinements,input_tokens,output_tokens,api_calls,seconds,status,failure,evaluation_error" };
        foreach (var r in records)
            rows.Add(Row(r.RunId, r.Benchmark, r.BugId, r.Revision, r.Split, r.Configuration, r.Repeat, r.Top1, r.Top5,
                r.Result.Accepted, r.Result.FrameworkPlausible, r.CorrectRepair, r.Result.Candidates.Count, r.Result.RefinementIterations.Count,
                Tokens(r, true), Tokens(r, false), r.Result.ModelCalls.Count, r.Result.DurationSeconds, r.Result.FinalStatus, r.Result.ErrorMessage, r.EvaluationError));
        await File.WriteAllLinesAsync(Path.Combine(output, "records.csv"), rows, ct);
        var metrics = new Dictionary<string, Func<ExperimentRecord, double?>>
        {
            ["top1"] = r => Number(r.Top1), ["top5"] = r => Number(r.Top5),
            ["accepted"] = r => r.Result.FinalStatus is "setup-error" or "failed" or "llm-error" or "output-truncated" or "localization-only" or "ineligible" ? null : Number(r.Result.Accepted),
            ["framework_plausible"] = r => r.Result.FinalStatus is "failed" or "llm-error" or "output-truncated" or "cancelled" ? null : Number(r.Result.FrameworkPlausible),
            ["correct_repair"] = r => Number(r.CorrectRepair),
            ["conditional_correctness"] = r => r.Result.Accepted ? Number(r.CorrectRepair) : null,
            ["incorrect_acceptance"] = r => r.Result.Accepted && r.CorrectRepair is not null ? Number(!r.CorrectRepair.Value) : null,
            ["compilation_pass"] = r => AnyPass(r, "compilation"), ["static_pass"] = r => AnyPass(r, "static"),
            ["input_tokens"] = r => Tokens(r, true), ["output_tokens"] = r => Tokens(r, false),
            ["total_tokens"] = r => Tokens(r, true) + Tokens(r, false),
            ["api_calls"] = r => r.Result.FinalStatus == "setup-error" ? null : r.Result.ModelCalls.Count,
            ["seconds"] = r => r.Result.FinalStatus == "setup-error" ? null : r.Result.DurationSeconds,
            ["refinements"] = r => r.Result.FinalStatus == "setup-error" ? null : r.Result.RefinementIterations.Count,
            ["compilation_seconds"] = r => ToolSeconds(r, false), ["static_seconds"] = r => ToolSeconds(r, true)
        };
        var table = new List<string> { "split,configuration,benchmark,metric,defects,observed_defects,missing_defects,successes_single_run,mean,percent_if_rate,repeats_min,repeats_max,median,q1,q3,run_ids" };
        foreach (var split in records.GroupBy(r => (r.Split, r.Configuration)))
        foreach (var benchmark in split.Select(r => r.Benchmark).Distinct().Append("Overall"))
        {
            var selected = split.Where(r => benchmark == "Overall" || r.Benchmark == benchmark).ToArray();
            foreach (var (metric, selector) in metrics)
            {
                var defects = selected.GroupBy(r => (r.Benchmark, r.BugId)).ToArray();
                // Incomplete repeats remain missing rather than silently averaging fewer runs.
                var values = defects.Select(g => Complete(g) && g.All(r => selector(r) is not null) ? (double?)g.Average(r => selector(r)!.Value) : null).ToArray();
                var observed = values.Where(v => v is not null).Select(v => v!.Value).Order().ToArray();
                var rate = !new[] { "input_tokens", "output_tokens", "total_tokens", "api_calls", "seconds", "refinements", "compilation_seconds", "static_seconds" }.Contains(metric);
                var mean = observed.Length == 0 ? (double?)null : observed.Average();
                table.Add(Row(split.Key.Split, split.Key.Configuration, benchmark, metric, defects.Length, observed.Length, defects.Length - observed.Length,
                    rate && observed.Length > 0 && defects.All(g => g.Count() == 1) ? observed.Sum() : null, mean, rate ? mean * 100 : null,
                    defects.Min(g => g.Count()), defects.Max(g => g.Count()), Quantile(observed, .5), Quantile(observed, .25), Quantile(observed, .75),
                    string.Join(';', selected.Select(r => r.RunId))));
            }
        }
        await File.WriteAllLinesAsync(Path.Combine(output, "tables.csv"), table, ct);
        var candidateRows = new List<string> { "run_id,benchmark,bug_id,configuration,method_id,refinement,accepted,first_failed_rule,check_rule,passed,message" };
        foreach (var r in records)
        foreach (var c in r.Result.Candidates)
        foreach (var check in c.Validation.Checks)
            candidateRows.Add(Row(r.RunId, r.Benchmark, r.BugId, r.Configuration, c.MethodId, c.Refinement, c.Accepted,
                c.Validation.Checks.FirstOrDefault(v => !v.Passed)?.Rule, check.Rule, check.Passed, check.Message));
        await File.WriteAllLinesAsync(Path.Combine(output, "candidates.csv"), candidateRows, ct);
        await File.WriteAllTextAsync(Path.Combine(output, "provenance.json"), JsonSerializer.Serialize(new {
            aggregation = "Within-defect mean across complete repeats, then equal-weight defect mean; missing observations excluded and counted explicitly. Overall pools defects, not dataset percentages.",
            records = records.Select(r => new { r.RunId, r.ManifestHash, r.SourceHashes, r.Result.LogPath })
        }, JsonLog.Options), ct);
        var failureRows = new List<string> { "split,configuration,benchmark,category,unsuccessful_observations,category_observations,percent,run_ids" };
        foreach (var group in records.Where(r => r.CorrectRepair == false).GroupBy(r => (r.Split, r.Configuration, r.Benchmark)))
        foreach (var category in group.GroupBy(FailureCategory))
            failureRows.Add(Row(group.Key.Split, group.Key.Configuration, group.Key.Benchmark, category.Key, group.Count(), category.Count(), 100.0 * category.Count() / group.Count(), string.Join(';', category.Select(r => r.RunId))));
        await File.WriteAllLinesAsync(Path.Combine(output, "failure_summary.csv"), failureRows, ct);
        var rejectionRows = new List<string> { "split,configuration,benchmark,rule,rejected_candidates,rule_candidates,percent,run_ids" };
        foreach (var group in records.SelectMany(r => r.Result.Candidates.Where(c => !c.Accepted).Select(c => new { Record = r, Candidate = c }))
            .GroupBy(x => (x.Record.Split, x.Record.Configuration, x.Record.Benchmark)))
        foreach (var rule in group.GroupBy(x => x.Candidate.Validation.Checks.FirstOrDefault(c => !c.Passed)?.Rule ?? "unknown"))
            rejectionRows.Add(Row(group.Key.Split, group.Key.Configuration, group.Key.Benchmark, rule.Key, group.Count(), rule.Count(), 100.0 * rule.Count() / group.Count(), string.Join(';', rule.Select(x => x.Record.RunId).Distinct())));
        await File.WriteAllLinesAsync(Path.Combine(output, "rejection_summary.csv"), rejectionRows, ct);
        var paired = new List<string> { "split,configuration_a,configuration_b,metric,paired_defects,a_only_success,b_only_success,mean_difference_pp,approx_ci95_low_pp,approx_ci95_high_pp,run_ids" };
        foreach (var split in records.GroupBy(r => r.Split))
        {
            var names = split.Select(r => r.Configuration).Distinct().Order().ToArray();
            for (var i = 0; i < names.Length; i++) for (var j = i + 1; j < names.Length; j++)
            foreach (var metric in new[] { "top1", "top5", "framework_plausible", "correct_repair" })
            {
                var selector = metrics[metric];
                var left = split.Where(r => r.Configuration == names[i]).GroupBy(r => (r.Benchmark, r.BugId)).ToDictionary(g => g.Key, g => g.ToArray());
                var right = split.Where(r => r.Configuration == names[j]).GroupBy(r => (r.Benchmark, r.BugId)).ToDictionary(g => g.Key, g => g.ToArray());
                var pairs = left.Keys.Intersect(right.Keys).Where(k => Complete(left[k]) && Complete(right[k]) && left[k].Select(r => r.Repeat).Order().SequenceEqual(right[k].Select(r => r.Repeat).Order()) &&
                    left[k].Concat(right[k]).All(r => selector(r) is not null)).ToArray();
                var differences = pairs.Select(k => left[k].Average(r => selector(r)!.Value) - right[k].Average(r => selector(r)!.Value)).ToArray();
                double? delta = differences.Length == 0 ? null : differences.Average();
                double? se = differences.Length < 2 ? null : Math.Sqrt(differences.Sum(d => Math.Pow(d - delta!.Value, 2)) / (differences.Length - 1) / differences.Length);
                paired.Add(Row(split.Key, names[i], names[j], metric, pairs.Length, differences.Count(d => d == 1), differences.Count(d => d == -1),
                    delta * 100, (delta - 1.96 * se) * 100, (delta + 1.96 * se) * 100,
                    string.Join(';', pairs.SelectMany(k => left[k].Concat(right[k])).Select(r => r.RunId))));
            }
        }
        await File.WriteAllLinesAsync(Path.Combine(output, "paired.csv"), paired, ct);
    }
    private static string FailureCategory(ExperimentRecord r)
    {
        if (r.Result.Accepted) return "accepted-but-retrospectively-incorrect";
        if (r.SearchHit == false) return "localization-miss";
        if (r.Result.FinalStatus == "budget-exhausted") return "inference-budget-exhausted";
        if (r.Result.Candidates.Count == 0 || r.Result.Candidates.All(c => !c.Validation.PatchApplication.Applied)) return "no-usable-patch";
        var last = r.Result.Candidates.LastOrDefault();
        if (last?.Validation.Checks.Any(c => c.Rule == "compilation" && !c.Passed) == true) return "compilation-failure";
        if (last?.Validation.Checks.Any(c => !c.Passed) == true) return "validation-rejection";
        return "search-exhausted";
    }
    private static double? ToolSeconds(ExperimentRecord r, bool analysis)
    {
        var seconds = new List<double>();
        foreach (var check in r.Result.Candidates.SelectMany(c => c.Validation.Checks))
        {
            if (check.Data is null) continue;
            var data = JsonSerializer.SerializeToElement(check.Data, JsonLog.Options);
            if (!analysis && check.Rule is "compilation" or "baseline-compilation" && data.TryGetProperty("seconds", out var value)) seconds.Add(value.GetDouble());
            if (analysis && check.Rule == "static")
                foreach (var side in new[] { "before", "after" })
                    if (data.TryGetProperty(side, out var item) && item.TryGetProperty("seconds", out var time)) seconds.Add(time.GetDouble());
        }
        return seconds.Count == 0 ? null : seconds.Sum();
    }
    private static bool Complete(IEnumerable<ExperimentRecord> records)
    {
        var array = records.ToArray();
        return array.Select(r => r.ExpectedRepeats).Distinct().Count() == 1 && array.Select(r => r.Repeat).Order().SequenceEqual(Enumerable.Range(1, array[0].ExpectedRepeats));
    }
    public static int? Tokens(ExperimentRecord r, bool input) => r.Result.ModelCalls.Count == 0
        ? r.Result.FinalStatus is "localization-only" or "ineligible" or "unrepaired" ? 0 : null
        : r.Result.ModelCalls.Any(c => (input ? c.InputTokens : c.OutputTokens) is null) ? null
        : r.Result.ModelCalls.Sum(c => (input ? c.InputTokens : c.OutputTokens)!.Value);
    private static double? AnyPass(ExperimentRecord r, string rule)
    {
        var checks = r.Result.Candidates.SelectMany(c => c.Validation.Checks).Where(c => c.Rule == rule).ToArray();
        return checks.Length == 0 ? null : checks.Any(c => c.Passed) ? 1 : 0;
    }
    private static double? Number(bool? value) => value is null ? null : value.Value ? 1 : 0;
    private static double? Quantile(double[] sorted, double p)
    {
        if (sorted.Length == 0) return null;
        var index = p * (sorted.Length - 1); var low = (int)index;
        return sorted[low] + (sorted[(int)Math.Ceiling(index)] - sorted[low]) * (index - low);
    }
    public static string Row(params object?[] values) => string.Join(',', values.Select(v => '"' +
        (Convert.ToString(v, CultureInfo.InvariantCulture) ?? "").Replace("\"", "\"\"") + '"'));
}
