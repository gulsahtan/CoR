# Running CoR experiments

The experiment runner executes configured benchmark cases and exports measured results. The repository contains source code, input examples and configuration templates; generated results stay in local ignored directories.

## 1. Install and verify tools

From the repository root:

```powershell
dotnet restore ChainOfRepair.sln
dotnet build ChainOfRepair.sln
dotnet run --project tests/ChainOfRepair.Tests
python -m unittest discover -s tests/python -v
```

Use .NET 9 SDK (C# 13) and Python 3.10+. The official `OpenAI` NuGet package is pinned to 2.14.0. The Python helper scripts use only the standard library. Java needs the JDK version required by the pinned benchmark release. C needs clang/gcc and clang-tidy. Python experiments need an actual Python installation (the Windows Store alias is not sufficient), pytest and Ruff. Java static analysis needs SpotBugs and all project dependencies. Record executable versions and checks in your study archive. Tool installation alone does not demonstrate benchmark compatibility.

Install Defects4J according to its [official repository](https://github.com/rjust/defects4j), checking out the specific release required for your study. The paper used v1.2, not an unspecified current release. Defects4J's Unix-oriented toolchain is best run in a Linux/WSL environment with the matching .NET/Python/JDK tools installed there; do not assume a Windows path is valid inside WSL. QuixBugs sources and tests are in the [official repository](https://github.com/jkoppel/QuixBugs). IntroClass is in its [official repository](https://github.com/ProgramRepair/IntroClass). Pin all checkouts to commit IDs. The web Dockerfile is not a complete benchmark environment.

## 2. Configure the API key locally

`OPENAI_API_KEY` is the only credential source. Never paste the key into a manifest, prompt, command argument, source file or log. To set it in the current PowerShell process without putting its value in command history:

```powershell
$corSecret = Read-Host 'OpenAI API key' -AsSecureString
$env:OPENAI_API_KEY = [System.Net.NetworkCredential]::new('', $corSecret).Password
```

Set the model in the manifest. Default `gpt-4o`, temperature 0.2, output maximum 2048, total budget 16000 and call maximum 20 preserve the requested model family and limits. For a new study choose one available GPT-4o snapshot explicitly and keep it fixed across configurations. Consult the [official model page](https://developers.openai.com/api/docs/models/gpt-4o) for availability and [official SDK guidance](https://developers.openai.com/api/docs/libraries). There is no silent model fallback.

Every HTTP attempt is counted. Automatic transport retries are disabled. If a response ends with `Length`, the reasoning stage makes at most one explicit compact regeneration under the same per-call and cumulative limits. Both attempts, measured usage and the incomplete text are retained; incomplete JSON/diffs are never passed to patch application. `output-truncated` means both stage attempts were incomplete, not a valid repair. Failed requests have null token usage, not guessed zero. Once usage is unknown the run stops. Budget admission reserves a conservative UTF-8 byte upper bound plus framing and maximum output; recorded usage always comes from the API. This may stop earlier than an exact tokenizer would. Large candidate methods are rejected by admission rather than silently cut in half.

## 3. Prepare real benchmark cases

Start with `experiments/manifest.template.json` and add actual cases from the three `*.case.template.json` files. `REPLACE...` values are not runnable data. A case needs:

* `benchmark`: Defects4J, QuixBugs or IntroClass; `bugId`: actual identity; `revision`: actual pinned buggy revision.
* `projectDirectory`: **repair-only** project copy, with no benchmark tests, expected outcomes or reference implementations. It must contain production sources and build dependencies/configuration needed for compilation. Review build scripts to ensure that they do not run tests or read hidden reference artifacts.
* `sourceFiles`: all eligible production source paths relative to that directory, not just the file known to be faulty. Restricting inputs using a reference fix leaks localization ground truth. Each file is indexed and candidates are ranked jointly. A generated patch still modifies only one method/file.
* `evaluationDirectory`: a separate complete benchmark checkout with tests. It is opened only after repair terminates; the final patch and unchanged source artifacts are overlaid onto an isolated copy. Original checkouts are not edited.
* Optional genuine non-test `bugReportFile`, `stackTraceFile`, `historyFile`, `defectTime`. Do not regenerate a trace using benchmark tests or pass assertion expected values. For code-only benchmarks leave these null. History includes only reliably mapped pre-defect commits; see `ranking.md`.
* `referenceMethodsFile`: a JSON array of qualified method IDs modified by the reference fix, used **only after termination**. IDs match `ranked_methods.json` (`relative/file::context.signature`). Audit this mapping independently. Missing labels stay null; they are not localization misses.
* `build`, `staticAnalysis`, `retrospectiveTests`: explicit commands, argument arrays and timeouts. `{file}` and `{project}` are substituted as single arguments; no shell interpolation is performed. Commands execute inside isolated project copies. Use absolute paths to helper scripts/tools when needed. Paths in the manifest resolve relative to the manifest; command executable paths must be valid in the execution environment.

Example build commands:

```json
{"executable":"defects4j","arguments":["compile"],"timeoutSeconds":120}
{"executable":"python","arguments":["-m","py_compile","{file}"],"timeoutSeconds":120}
{"executable":"clang","arguments":["-fsyntax-only","{file}"],"timeoutSeconds":120}
```

For Defects4J, verify that the chosen compile command/build files work in the repair-only tree without test sources. If the benchmark compile target depends on tests, provide a production-only build command instead; never relax the artifact policy silently. Multi-file Java projects require their original classpath/build configuration. For IntroClass preserve compiler flags and libraries from the original case.

The static command must print **only a JSON array** on stdout, including `[]` for a completed clean analysis. Exit nonzero on missing tools or analysis failures. Each finding is `{tool, checkId, file, method, category, severity}`. Severity increases with importance; default rejection threshold is 2. Canonical identities omit changing line numbers, compare duplicate counts and reject new/worsened findings. New categories are distinct identities. The provided adapters run real analyzers:

```text
python /absolute/CoR/scripts/static_analysis.py spotbugs -- /path/to/compiled/classes
python /absolute/CoR/scripts/static_analysis.py clang-tidy -- source.c -checks=clang-analyzer-*,bugprone-* -- -std=c99
python /absolute/CoR/scripts/static_analysis.py ruff -- python_programs
```

Pin check sets and tool versions. SpotBugs priority 1/2/3 maps to severity 3/2/1. clang warnings/errors map to 2/3; Ruff findings map to 2. C method mapping is heuristic and must be audited for macros/multiline signatures; Python Ruff findings are canonicalized at module level. This is not equivalent analyzer coverage across languages. Exclude tests from Ruff targets.

The retrospective command contract is **exit 0 = all required tests passed, 1 = behavioral failure, any other exit/timeout = infrastructure error**. Missing or failed infrastructure stays null. A wrapper normalizes common conventions:

```text
python /absolute/CoR/scripts/oracle.py defects4j -- defects4j test
python /absolute/CoR/scripts/oracle.py pytest -- python -m pytest python_testcases/test_PROGRAM.py
python /absolute/CoR/scripts/oracle.py command -- /absolute/your-introclass-oracle
```

IntroClass oracle scripts must compile the accepted program and run every required input/output test from the chosen suite, returning 2 for compilation/infrastructure errors. The repository cannot infer which black-box/white-box test protocol you intend; specify it before the study. Test access never affects patch selection. `correctRepair` is test-suite adequacy, not a proof of general correctness.

## 4. Select and freeze a new sample

Create a catalog manifest containing real prepared cases. The default new-study allocation is 150 Defects4J + 30 QuixBugs + 150 IntroClass evaluation defects, and 10 per benchmark for calibration. It intentionally differs from the paper: all 40 QuixBugs defects cannot be in evaluation and also supply 10 disjoint calibration defects. Override quotas only with an explicit documented protocol.

```powershell
python scripts/select_sample.py experiments/local/catalog.json experiments/local/new-study --seed cor-new-study-v1
python scripts/experiment_matrix.py experiments/local/new-study/calibration.json experiments/local/new-study/calibration-grid.json --suite calibration
python scripts/experiment_matrix.py experiments/local/new-study/evaluation.json experiments/local/new-study/evaluation-grid.json --suite evaluation
```

Selection orders actual case IDs by SHA-256 of seed/benchmark/ID, checks local sources, refuses insufficient populations and records the catalog hash/IDs. No outcomes enter selection. No output directory is overwritten. Use one language implementation per QuixBugs identity unless the design explicitly models paired implementations. Freeze samples and settings before inference; calibration cases never enter evaluation.

The matrix supplies full CoR, iterative direct prompting, eligible lexical localization, validation ablation, progressive artifacts, artifact removals and specified validation-rule removals. Calibration includes the paper's weight vectors, k=3/5/10 and refinement limits 1/3/5. The default row supplies k=5/refinement=3. Direct prompting is a defined intervention, not an invented extra 'without reasoning' condition.

## 5. Run one case, then the batch

```powershell
dotnet run --project src/ChainOfRepair.Cli -- --manifest experiments/local/new-study/evaluation-grid.json --case REAL_ID --configuration full-cor --output experiments/results
dotnet run --project src/ChainOfRepair.Cli -- --manifest experiments/local/new-study/evaluation-grid.json --output experiments/results
```

Each invocation writes a unique batch directory, a manifest snapshot, environment metadata, source hashes, per-run JSON, prompts and candidates, plus CSV reports. No run is overwritten. Nonzero CLI exit reports setup/API/evaluation infrastructure errors; repair rejection itself is a valid experimental outcome. Full CoR status `framework-plausible` means internal acceptance only. `accepted-ablation` does not assert full plausibility. `budget-exhausted` is distinct from `unrepaired` and from API failures.

Inspect method extraction and audit one real build/static/oracle execution before launching the batch. This update did not execute paid model calls or benchmark experiments. Tool integration is validated with offline regression tests, not claimed benchmark success.

## 6. Generate and interpret tables

```powershell
dotnet run --project src/ChainOfRepair.Cli -- report experiments/results/BATCH experiments/results/BATCH/reports
```

`records.csv` contains individual runs; `tables.csv` groups by split/configuration/benchmark and pools defects for Overall; `candidates.csv` preserves per-attempt decisions; `paired.csv` joins identical defect IDs; `provenance.json` maps every included run. `failure_summary.csv` groups observed unsuccessful runs using a documented precedence (accepted failure, search miss, inference budget, patch failure, compile failure, validation rejection); repeated runs here remain labeled observations. `rejection_summary.csv` counts the first failed rule per rejected candidate. Missing values are null in JSON and blank in CSV. Denominators include only observed outcomes and expose missing counts. Repeated runs are averaged within each defect first; incomplete repeats are missing. Do not interpret partial-coverage observed-only rates as full-study rates.




## Web and sample CLI

The web UI and single-file CLI remain available, but cannot claim full plausibility without real static analysis. Configure `Validation` server-side in ASP.NET configuration (the `ValidationConfiguration` schema), or pass `--validation path/to/validation.json` to the sample CLI. Do not accept executable paths from untrusted web users. The browser input is non-test artifacts only. The manifest CLI is the primary experimental interface.

Archive the new study's code commit, uncommitted diff if any, manifests, benchmark revisions, tool versions, prompt hashes, model IDs and result directory together. Keep secrets and licensed datasets outside Git. Generated data directories and `.env` files are ignored by default.
