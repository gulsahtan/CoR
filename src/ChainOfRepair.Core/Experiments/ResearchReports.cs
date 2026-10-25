using System.Text.Json;
using ChainOfRepair.Core.Models;

namespace ChainOfRepair.Core.Experiments;

public static class ResearchReports
{
    public static decimal? Cost(ModelCall c, ApiPricing? p)
    {
        if (p is null || c.ResponseModel != p.Model || c.InputTokens is null || c.OutputTokens is null || c.CachedInputTokens is null) return null;
        if (c.CachedInputTokens < 0 || c.CachedInputTokens > c.InputTokens) return null;
        return ((c.InputTokens.Value - c.CachedInputTokens.Value) * p.InputPerMillion + c.CachedInputTokens.Value * p.CachedInputPerMillion + c.OutputTokens.Value * p.OutputPerMillion) / 1000000m;
    }
    public static bool? TestPass(CommandOutcome? t) => t is null ? null : t.ExitCode == 0 && t.Error is null ? true : t.ExitCode == 1 ? false : null;
    public static int? FailedTests(CommandOutcome? t)
    {
        if (t is null) return null;
        foreach (var line in t.Output.Split('\n').Reverse())
            try { using var d = JsonDocument.Parse(line); if (d.RootElement.TryGetProperty("failed", out var n) && n.TryGetInt32(out var value) && value >= 0) return value; }
            catch (JsonException) { }
        return null;
    }
    public static bool? Compiled(ExperimentRecord r)
    {
        var check = r.Result.FinalValidation.Checks.LastOrDefault(c => c.Rule == "compilation");
        if (check?.Data is null) return null;
        var outcome = JsonSerializer.Deserialize<CommandOutcome>(JsonSerializer.Serialize(check.Data, JsonLog.Options), JsonLog.Options);
        return outcome?.ExitCode is null ? null : outcome.Passed;
    }
    public static decimal? TotalCost(ExperimentRecord r) => r.Result.ModelCalls.Count == 0 ? null : r.Result.ModelCalls.All(c => c.EstimatedCostUsd is not null) ? r.Result.ModelCalls.Sum(c => c.EstimatedCostUsd!.Value) : null;
    public static async Task SaveConfigAsync(ExperimentManifest manifest, string output, string prompts, ExperimentCase[] cases, ExperimentConfiguration[] configs, string manifestRoot, CancellationToken ct)
    {
        var snapshot = Path.Combine(output, "snapshot"); Directory.CreateDirectory(snapshot);
        var hashes = new Dictionary<string, string>();
        foreach (var folder in new[] { "src", "scripts", "tests", "prompts" })
        {
            var source = folder == "prompts" ? prompts : Path.GetFullPath(folder);
            if (!Directory.Exists(source)) continue;
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Where(p => !p.Split(Path.DirectorySeparatorChar).Any(s => s is "bin" or "obj" or "__pycache__") && new[] { ".cs", ".csproj", ".py", ".md", ".sh", ".ps1" }.Contains(Path.GetExtension(p))))
            {
                var relative = Path.Combine(folder, Path.GetRelativePath(source, file));
                var dest = Path.Combine(snapshot, relative); Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest, true); hashes[relative] = ExperimentRunner.Hash(await File.ReadAllTextAsync(file, ct));
            }
        }
        var inputHashes = new Dictionary<string, string>();
        for (var i = 0; i < cases.Length; i++)
        {
            var item = cases[i]; if (item.SetupError is not null) continue;
            foreach (var (role, directory) in new[] { ("repair", (string?)item.ProjectDirectory), ("evaluation", item.EvaluationDirectory), ("independent", item.IndependentEvaluationDirectory) })
            {
                if (directory is null) continue;
                var root = Path.GetFullPath(directory, manifestRoot);
                if (!Directory.Exists(root)) continue;
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(f => !f.Split(Path.DirectorySeparatorChar).Any(s => s is ".git" or "bin" or "obj" or "__pycache__")))
                {
                    var relative = Path.Combine("inputs", i.ToString("D3"), role, Path.GetRelativePath(root, file));
                    var dest = Path.Combine(snapshot, relative); Directory.CreateDirectory(Path.GetDirectoryName(dest)!); File.Copy(file, dest, true);
                    inputHashes[relative] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(file, ct)));
                }
            }
            foreach (var file in new[] { item.ReferenceMethodsFile, item.RcaReviewsFile, item.BugReportFile, item.StackTraceFile, item.HistoryFile }.Where(f => f is not null))
            {
                var source = Path.GetFullPath(file!, manifestRoot); if (!File.Exists(source)) continue;
                var dest = Path.Combine(snapshot, "inputs", i.ToString("D3"), "metadata", Path.GetFileName(source));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!); File.Copy(source, dest, true);
                inputHashes[Path.GetRelativePath(snapshot, dest)] = ExperimentRunner.Hash(await File.ReadAllTextAsync(source, ct));
            }
        }
        await File.WriteAllTextAsync(Path.Combine(output, "experiment_config.json"), JsonSerializer.Serialize(new {
            manifest, selectedCases = cases.Select(c => new { c.Benchmark, c.ProjectName, c.BugId, c.Revision }),
            selectedConfigurations = configs, plannedRuns = cases.Length * configs.Length * manifest.Repeats,
            inputHashes, implementationHashes = hashes, codeVersion = ExperimentRunner.Hash(JsonSerializer.Serialize(hashes)),
            promptVersion = ExperimentRunner.Hash(JsonSerializer.Serialize(hashes.Where(h => h.Key.StartsWith("prompts")))),
            startedUtc = DateTimeOffset.UtcNow, costs = "Estimated USD from measured cached/uncached/output tokens; not invoice charges.",
            missing = "Blank CSV cells and JSON null are unobserved, never zero. Rates use observed denominators and report planned/missing separately."
        }, JsonLog.Options), ct);
    }
    public static async Task WriteAsync(IReadOnlyList<ExperimentRecord> records, string output, CancellationToken ct)
    {
        string? promptVersion = null;
        var configPath = Path.Combine(output, "experiment_config.json");
        if (File.Exists(configPath))
        {
            using var config = JsonDocument.Parse(await File.ReadAllTextAsync(configPath, ct));
            if (config.RootElement.TryGetProperty("promptVersion", out var version)) promptVersion = version.GetString();
        }
        string Row(params object?[] a) => ExperimentReports.Row(a);
        async Task Save(string name, string header, IEnumerable<string> rows) => await File.WriteAllLinesAsync(Path.Combine(output, name), new[] { header }.Concat(rows), ct);
        await Save("experiments.csv", "run_id,benchmark,project,bug_id,revision,split,configuration,repeat,expected_repeats,config_hash,prompt_version,requested_model,response_models,top1,top3,top5,root_cause_correct,generated_patch_count,final_compiled,benchmark_tests_passed,failed_tests,independently_verified,input_tokens,output_tokens,total_tokens,estimated_cost_usd,seconds,status,error,evaluation_error,baseline_exit",
            records.Select(r => Row(r.RunId,r.Benchmark,r.ProjectName,r.BugId,r.Revision,r.Split,r.Configuration,r.Repeat,r.ExpectedRepeats,r.ManifestHash,r.PromptVersion ?? promptVersion,r.Result.ModelName,string.Join(';',r.Result.ModelCalls.Select(c=>c.ResponseModel).Distinct()),r.Top1,r.Top3,r.Top5,r.RootCauseCorrect,PatchCount(r),Compiled(r),TestPass(r.Tests),FailedTests(r.Tests),r.IndependentlyVerified,ExperimentReports.Tokens(r,true),ExperimentReports.Tokens(r,false),ExperimentReports.Tokens(r,true)+ExperimentReports.Tokens(r,false),TotalCost(r),r.ExperimentSeconds,r.Result.FinalStatus,r.Result.ErrorMessage,r.EvaluationError,r.BaselineTests?.ExitCode)));
        await Save("token_usage.csv", "run_id,call_number,stage,response_model,input_tokens,cached_input_tokens,output_tokens,total_tokens,limit,finish_reason,retry_count,seconds,error_kind,error",
            records.SelectMany(r=>r.Result.ModelCalls.Select(c=>Row(r.RunId,c.Number,c.Stage,c.ResponseModel,c.InputTokens,c.CachedInputTokens,c.OutputTokens,c.TotalTokens,c.MaxOutputTokens,c.FinishReason,c.RetryCount,c.Seconds,c.ErrorKind,c.Error))));
        await Save("api_costs.csv", "run_id,call_number,stage,response_model,estimated_cost_usd,cost_kind,price_source",
            records.SelectMany(r=>r.Result.ModelCalls.Select(c=>Row(r.RunId,c.Number,c.Stage,c.ResponseModel,c.EstimatedCostUsd,"estimate_not_invoice","experiment_config.json:manifest.pricing"))));
        await Save("fault_localization.csv", "run_id,benchmark,bug_id,top1,top3,top5,rank,method_id,score",
            records.SelectMany(r=>r.Result.Ranking.RankedMethods.Select((m,i)=>Row(r.RunId,r.Benchmark,r.BugId,r.Top1,r.Top3,r.Top5,i+1,m.Method.Id,m.FinalSuspiciousnessScore))));
        await Save("root_cause_results.csv", "run_id,exchange_index,attempt,complete,error_kind,prompt_hash,response_hash,response,verified_accuracy",
            records.SelectMany(r=>r.Result.Reasoning.Exchanges.Select((e,i)=>(e,i)).Where(x=>x.e.Stage=="root-cause").Select(x=>Row(r.RunId,x.i+1,x.e.Attempt,x.e.IsComplete,x.e.ErrorKind,x.e.PromptHash,ExperimentRunner.Hash(x.e.Response),x.e.Response,x.e.Response==r.Result.Reasoning.RootCause?r.RootCauseCorrect:null))));
        await Save("patch_results.csv", "run_id,candidate_index,method,refinement,accepted,applied,patch_json,validation_json,final_tests_passed,failed_tests,independently_verified",
            records.SelectMany(r=>r.Result.Candidates.Select((c,i)=>Row(r.RunId,i+1,c.MethodId,c.Refinement,c.Accepted,c.Validation.PatchApplication.Applied,c.PatchJson,JsonSerializer.Serialize(c.Validation,JsonLog.Options),c.Accepted?TestPass(r.Tests):null,c.Accepted?FailedTests(r.Tests):null,c.Accepted?r.IndependentlyVerified:null))));
        var rows = new List<string>();
        var report = new List<string> { "# Measured pilot experiment report", "", "Observed-only metrics are not full-benchmark accuracy. Repeats are correlated observations of the same defect, not independent defects. Missing outcomes are not successes or failures.", "", $"Unique selected bugs with records: {records.Select(r=>(r.Benchmark,r.BugId)).Distinct().Count()}; recorded runs: {records.Count}.", "" };
        foreach(var group in records.GroupBy(r=>(r.Split,r.Configuration,r.Benchmark)))
        {
            var a=group.ToArray(); var planned=a.GroupBy(r=>r.BugId).Sum(g=>g.Max(r=>r.ExpectedRepeats));
            report.Add($"## {group.Key.Benchmark} / {group.Key.Configuration} / {group.Key.Split}"); report.Add("");
            var metrics=new Dictionary<string,Func<ExperimentRecord,bool?>> {
                ["localization_top1"]=r=>r.Top1,["localization_top3"]=r=>r.Top3,["localization_top5"]=r=>r.Top5,
                ["root_cause_accuracy"]=r=>r.RootCauseCorrect,["compilation_success"]=Compiled,
                ["benchmark_test_pass_conditional"]=r=>TestPass(r.Tests),
                ["plausible_patch_rate"]=r=>r.Result.FinalStatus is "unrepaired" or "budget-exhausted" && !r.Result.Accepted ? false : TestPass(r.Tests),["independently_verified_correct"]=r=>r.IndependentlyVerified,
                ["retry_run_rate"]=r=>r.Result.ModelCalls.Count==0?null:r.Result.ModelCalls.Any(c=>c.RetryCount>0),
                ["failure_rate"]=r=>!r.Result.Accepted || TestPass(r.Tests)==false || r.EvaluationError is not null };
            foreach(var (name,f) in metrics) {
                var observed=a.Select(f).Where(x=>x.HasValue).ToArray(); var successes=observed.Count(x=>x==true); double? rate=observed.Length==0?null:(double)successes/observed.Length;
                rows.Add(Row(group.Key.Split,group.Key.Configuration,group.Key.Benchmark,name,"run_observation",planned,observed.Length,planned-observed.Length,successes,observed.Length,rate,null,null,null));
                report.Add($"- {name}: {successes}/{observed.Length} observed; {planned-observed.Length} missing of {planned} planned runs.");
            }
            var numeric=new Dictionary<string,Func<ExperimentRecord,double?>> {
                ["input_tokens"]=r=>ExperimentReports.Tokens(r,true),["output_tokens"]=r=>ExperimentReports.Tokens(r,false),
                ["estimated_cost_usd"]=r=>TotalCost(r) is {} c?(double)c:null,["seconds"]=r=>r.ExperimentSeconds };
            foreach(var (name,f) in numeric) {
                var vals=a.Select(f).Where(v=>v.HasValue).Select(v=>v!.Value).Order().ToArray();
                rows.Add(Row(group.Key.Split,group.Key.Configuration,group.Key.Benchmark,name,"run_observation",planned,vals.Length,planned-vals.Length,null,null,vals.Length==0?null:vals.Average(),Quantile(vals,.5),Quantile(vals,.25),Quantile(vals,.75)));
            }
            report.Add("");
        }
        await Save("summary_results.csv","split,configuration,benchmark,metric,unit,planned,observed,missing,numerator,denominator,mean_or_rate,median,q1,q3",rows);
        var calls=records.SelectMany(r=>r.Result.ModelCalls).ToArray();
        report.Add($"Unique bugs with LLM calls: {records.Where(r=>r.Result.ModelCalls.Count>0).Select(r=>(r.Benchmark,r.BugId)).Distinct().Count()}; unique bugs with candidate test results: {records.Where(r=>TestPass(r.Tests).HasValue).Select(r=>(r.Benchmark,r.BugId)).Distinct().Count()}; unique bugs localized Top-1 at least once: {records.Where(r=>r.Top1==true).Select(r=>(r.Benchmark,r.BugId)).Distinct().Count()}; verified correct RCA observations: {records.Count(r=>r.RootCauseCorrect==true)}/{records.Count(r=>r.RootCauseCorrect.HasValue)}.");
        report.Add($"Generated complete patch responses: {records.Sum(PatchCount)}; accepted patches: {records.Count(r=>r.Result.Accepted)}; final patches compiled: {records.Count(r=>Compiled(r)==true)}; test-passing patches: {records.Count(r=>TestPass(r.Tests)==true)}; independently verified: {records.Count(r=>r.IndependentlyVerified==true)} (observed {records.Count(r=>r.IndependentlyVerified.HasValue)}).");
        report.Add($"Measured token subtotal: {calls.Where(c=>c.TotalTokens.HasValue).Sum(c=>c.TotalTokens!.Value)}; calls with unknown usage: {calls.Count(c=>!c.TotalTokens.HasValue)}. Estimated API cost subtotal (USD, not invoice): {calls.Where(c=>c.EstimatedCostUsd.HasValue).Sum(c=>c.EstimatedCostUsd!.Value):F6}; unpriced calls: {calls.Count(c=>!c.EstimatedCostUsd.HasValue)}.");
        report.Add($"Sum of experiment execution seconds: {records.Sum(r=>r.ExperimentSeconds):F3}; whole batch wall time: batch_timing.json. Length failures: {calls.Count(c=>c.FinishReason=="Length")}; retry calls: {calls.Count(c=>c.RetryCount>0)}.");
        report.Add(""); report.Add("## Failures and limitations"); report.Add("");
        foreach(var r in records.Where(r=>!r.Result.Accepted || TestPass(r.Tests)==false || r.EvaluationError is not null))
            report.Add($"- {r.Benchmark}/{r.BugId}, repeat {r.Repeat}: {r.Result.FinalStatus}; {r.Result.ErrorMessage}; {r.EvaluationError}; failed tests={FailedTests(r.Tests)?.ToString()??"unobserved"}. See {r.RunId}.record.json.");
        report.Add("Root-cause accuracy requires a reviewer, evidence and exact response hash. Benchmark tests do not establish independent correctness. Source and prompt snapshots accompany this batch. Public benchmark exposure and pilot prompt development prevent claims of training-data independence.");
        await File.WriteAllLinesAsync(Path.Combine(output,"report.md"),report,ct);
        await File.WriteAllTextAsync(Path.Combine(output,"report_generation.json"), JsonSerializer.Serialize(new { generatedUtc = DateTimeOffset.UtcNow, recordIds = records.Select(r=>r.RunId), exporterVersion = typeof(ResearchReports).Assembly.ManifestModule.ModuleVersionId }, JsonLog.Options), ct);
    }
    public static int PatchCount(ExperimentRecord r)=>r.Result.Reasoning.Exchanges.Count(e=>e.IsComplete && e.Stage is "patch-synthesis" or "refinement" or "direct-patch");
    private static double? Quantile(double[] a,double p) { if(a.Length==0)return null;var x=p*(a.Length-1);var i=(int)x;return a[i]+(a[(int)Math.Ceiling(x)]-a[i])*(x-i); }
}
