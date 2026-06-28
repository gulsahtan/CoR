# Chain-of-Repair (CoR) AI Application

Chain-of-Repair (CoR) is a C#/.NET AI Application for artifact-aware automated program repair. It combines source-code parsing, failing-output analysis, suspicious-method ranking, LLM-driven root-cause analysis, LLM-driven patch synthesis, static validation, bounded LLM refinement, and reproducibility logging.

This repository is intended to accompany a PeerJ Computer Science AI Application submission. It provides the code, prompt templates, sample inputs, dataset provenance notes, and execution instructions needed for review and reproduction.

## Description

CoR accepts:

- source code,
- failing test output, compiler output, or stack trace text,
- an optional bug description,
- a programming language selection,
- and a Top-K localization depth.

For each repair request, CoR executes the following production pipeline:

1. Parse the input source code.
2. Extract candidate methods or functions.
3. Parse failing output, compiler diagnostics, stack trace frames, and optional bug description.
4. Rank candidate methods using artifact-aware Weighted Borda Count.
5. Select the Top-K suspicious methods and localize the highest-ranked method.
6. Send the localized method and artifacts to the LLM for root-cause analysis.
7. Send the root-cause JSON back to the LLM for patch synthesis.
8. Apply the LLM-generated unified diff to the original source code.
9. Validate the patched code without running tests.
10. If validation fails, send the validation failures back to the LLM for refinement.
11. Repeat refinement for at most three iterations.
12. Generate an LLM-based developer explanation.
13. Save prompts, responses, patches, validation results, and final status under `logs/runs/{runId}/`.

The production pipeline uses real LLM calls through `OpenAiLLMClient`. It does not use hard-coded root causes, canned patches, placeholder diffs, or silent fallback responses. If `OPENAI_API_KEY` is missing or the OpenAI call fails, the application returns a clear error to the UI and logs.

## Article Type and Reproducibility Context

This project is documented as an AI Application artifact rather than a conventional research article artifact. The repository addresses the PeerJ AI Application requirements by providing:

- algorithms and code used to implement the system,
- a plain-text README with implementation steps,
- dataset source information,
- code structure and usage instructions,
- computing infrastructure details,
- preprocessing and methodology notes,
- tests and reproducibility logs,
- license information.

For a single-author submission, the author should confirm in PeerJ's Confidential Information for PeerJ Staff whether the single-author listing is intentional. If additional contributors should be listed, update the manuscript and PeerJ author declaration before resubmission.

## Repository Contents

```text
ChainOfRepair.sln
src/
  ChainOfRepair.Core/       Core parsing, ranking, LLM orchestration, patching, validation, diagnostics
  ChainOfRepair.Web/        ASP.NET Core Razor Pages user interface
  ChainOfRepair.Cli/        Batch CLI runner for benchmark-style cases
tests/
  ChainOfRepair.Tests/      Dependency-light test runner
prompts/                    Runtime LLM prompt templates
docs/
  datasets.md               Dataset provenance and conversion notes
sample_inputs/              Small runnable example inputs
sample_outputs/             Example output location
logs/                       Runtime experiment logs
LICENSE                     MIT license
```

## Code Information

The main implementation is in `src/ChainOfRepair.Core`:

- `Parsing/ArtifactParsers.cs`: language-aware source and failing-output parsing.
- `Ranking/WeightedBordaRanker.cs`: artifact-aware Weighted Borda Count ranking.
- `Reasoning/LlmReasoning.cs`: `ILLMClient`, `OpenAiLLMClient`, prompt-template loading, and LLM stage orchestration.
- `Validation/PatchApplicationService.cs`: unified diff parsing and application to the original source code.
- `Validation/ValidationEngine.cs`: syntax/compile checks, differential static validation, and causal alignment validation.
- `Pipeline/RepairPipeline.cs`: end-to-end repair pipeline and run logging.
- `Models/CoRModels.cs`: request, ranking, LLM, validation, and pipeline result models.

The web app is in `src/ChainOfRepair.Web`, and the command-line runner is in `src/ChainOfRepair.Cli`.

## Dataset Information

This repository contains a small sample case in `sample_inputs/` for smoke testing. Larger benchmark experiments can be prepared from third-party automated program repair datasets. If any of these datasets are used in the manuscript experiments, cite their original sources in the Materials & Methods section and follow their licenses.

Third-party dataset sources:

- Defects4J: https://defects4j.org/ and https://github.com/rjust/defects4j
- QuixBugs: https://github.com/jkoppel/QuixBugs
- IntroClass: https://github.com/ProgramRepair/IntroClass

Dataset conversion format expected by the CLI:

```text
case-id/
  source.java | source.py | source.c | source.cs
  failing.txt
  bug.txt
```

`source.*` contains the buggy program or extracted source file. `failing.txt` contains failing test output, compiler output, or stack traces. `bug.txt` is optional and contains a natural-language bug description.

## Data Preprocessing

CoR does not require model training or dataset-specific feature precomputation. Preprocessing is performed at runtime for each input case:

1. Normalize source text into line-indexed form.
2. Extract candidate method/function regions using language-specific parsers.
3. Extract stack trace frames, compiler diagnostics, file names, method names, line numbers, and error types from failing output.
4. Detect local source-code signals such as null checks, boundary conditions, exception handling, type conversion, API calls, and memory/resource-management patterns.
5. Compute artifact-aware ranking features.
6. Serialize the selected method, source context, failing artifacts, and ranking metadata into LLM prompts.

If a benchmark dataset is converted manually into the `source.*`, `failing.txt`, and `bug.txt` layout, the conversion procedure and original dataset IDs should be reported in the manuscript.

## Methodology

### Localization

CoR ranks candidate methods/functions with a Weighted Borda Count aggregation:

```text
S_total(m) = Sum_i(w_i * phi_i(rank_i(m)))
```

Default artifact weights are:

- source-code context: `0.40`,
- failing output and stack trace: `0.30`,
- bug report: `0.20`,
- revision history placeholder: `0.10`.

If an artifact is missing, the available artifact weights are normalized to sum to one.

### LLM Stages

The runtime prompt templates are loaded from `prompts/`:

- `root_cause.md`
- `patch_synthesis.md`
- `self_consistency.md`
- `refinement.md`
- `explanation.md`

The LLM stages are:

1. Root-cause analysis: returns structured JSON with `faultCategory`, `rootCause`, `evidence`, `localizedMethod`, and `confidence`.
2. Patch synthesis: returns structured JSON with `patchType`, `targetMethod`, `changedLinesRationale`, `unifiedDiff`, and `expectedEffect`.
3. Self-consistency checking: returns structured JSON assessing causal alignment and scope.
4. Refinement: returns a corrected patch JSON when validation fails.
5. Developer explanation: returns a grounded explanation of the final repair result.

### Patch Application and Validation

CoR applies the generated unified diff to the original source code and records:

- original source code,
- patched source code,
- final unified diff,
- changed line ranges,
- application messages.

Validation is performed on the patched code, not dummy strings. It includes:

- syntax or compilation validation using available tools,
- differential static validation for newly introduced issues,
- causal alignment validation to check whether changes stay inside the localized method/function and match the inferred root cause.

The application does not run the project's tests during repair generation. Validation is intentionally static or compile/syntax based.

## Computing Infrastructure

The application was developed and evaluated using the following computing environment:

- Operating System: Microsoft Windows 10 (64-bit)
- CPU: Intel Core i7-11700F (8 cores, 16 threads)
- GPU: NVIDIA GeForce RTX 3070 (8 GB GDDR6)
- Memory: 32 GB RAM
- Runtime: .NET 9 SDK
- Programming Language: C# 13
- Web Framework: ASP.NET Core Razor Pages
- AI Integration: Official OpenAI .NET SDK
- Optional External Compilers:
  - Java: `javac`
  - Python: `python` / `python3`
  - C: `gcc` or `clang`

Internet connectivity is required only for live OpenAI API calls. Runtime diagnostics, including operating system, CPU architecture, .NET version, compiler configuration, model settings, and execution logs, are available through the Diagnostics page of the application.

## Requirements

Required:

- .NET 9 SDK
- An OpenAI API key in the `OPENAI_API_KEY` environment variable for live repair runs

Optional validation tools:

- Java: `javac`
- Python: `python` or `python3`
- C: `gcc` or `clang`
- C#: optional build/syntax wrapper configured through `COR_CSHARP_BUILD`

Environment variables:

- `OPENAI_API_KEY`: OpenAI API key. Required for production LLM calls.
- `COR_OPENAI_MODEL`: optional model override. Default: `gpt-4o`.
- `COR_JAVA_COMPILER`: optional path to `javac`.
- `COR_PYTHON`: optional path to `python` or `python3`.
- `COR_C_COMPILER`: optional path to `gcc` or `clang`.
- `COR_CSHARP_BUILD`: optional path to a C# syntax/build wrapper.

The default web configuration is:

```json
{
  "LLM": {
    "Provider": "OpenAI",
    "Model": "gpt-4o",
    "Temperature": 0.2,
    "MaxTokens": 2048,
    "UseMockClient": false
  }
}
```

`UseMockClient` is disabled by default. Demo/mock mode is not valid for reproducibility.

## Installation

From the repository root:

```powershell
dotnet restore ChainOfRepair.sln
dotnet build ChainOfRepair.sln
```

Set the OpenAI key for the current PowerShell session:

```powershell
$env:OPENAI_API_KEY = "your-openai-api-key"
```

Or set it persistently on Windows:

```powershell
setx OPENAI_API_KEY "your-openai-api-key"
```

Restart the terminal or application after using `setx`.

## Usage Instructions

### Web Application

```powershell
dotnet run --project src/ChainOfRepair.Web/ChainOfRepair.Web.csproj
```

Open the local URL printed by ASP.NET Core. The main page provides fields for:

- source code,
- failing test or compile output,
- bug description,
- language,
- Top-K.

After a run, the UI displays:

- real/mock LLM client status,
- selected model name,
- prompt templates used,
- root-cause JSON,
- patch JSON,
- self-consistency result,
- validation failures,
- refinement prompts and outputs,
- final patched source code,
- final unified diff,
- final developer explanation,
- run log location.

### Command-Line Interface

```powershell
dotnet run --project src/ChainOfRepair.Cli/ChainOfRepair.Cli.csproj -- --input sample_inputs --output sample_outputs --language Java --topk 5
```

Expected output:

- one JSON export per benchmark case,
- `summary.csv`,
- detailed run logs under the selected output folder.

### Tests

```powershell
dotnet run --project tests/ChainOfRepair.Tests/ChainOfRepair.Tests.csproj
```

The test runner covers:

- production DI registration of `OpenAiLLMClient`,
- clear failure when `OPENAI_API_KEY` is missing,
- dynamic prompt construction for different inputs,
- absence of canned patches,
- patch application to actual source code,
- validation on patched code,
- validation-error feedback during refinement,
- three-iteration refinement budget,
- per-stage run log creation,
- parser and ranking behavior.

## Reproducibility Logs

Every run writes logs under:

```text
logs/runs/{runId}/
```

The directory contains files such as:

```text
input.json
ranked_methods.json
root_cause_prompt.txt
root_cause_response.json
patch_prompt.txt
patch_response.json
self_consistency_prompt.txt
self_consistency_response.json
patched_code.txt
validation_result.json
refinement_1_prompt.txt
refinement_1_response.json
refinement_2_prompt.txt
refinement_2_response.json
refinement_3_prompt.txt
refinement_3_response.json
explanation_prompt.txt
explanation_response.txt
final_result.json
```

These logs are intended to demonstrate that prompts and responses are generated dynamically for each run.

## Code and Data Availability

For PeerJ review and publication, provide this repository as one of the following:

- a DOI-linked public repository, such as Zenodo archived from GitHub, or
- a supplemental file with a descriptive legend that includes the term `code` and is marked as the appropriate file type in the submission system.

All code required to build and run the application is included in this repository. Sample input data are included under `sample_inputs/`. Third-party datasets are not redistributed here; their original source URLs are listed above and in `docs/datasets.md`.

## Citations

If this repository is used with third-party datasets, cite the original dataset sources:

- Defects4J: https://defects4j.org/ and https://github.com/rjust/defects4j
- QuixBugs: https://github.com/jkoppel/QuixBugs
- IntroClass: https://github.com/ProgramRepair/IntroClass

Also cite the accompanying Chain-of-Repair AI Application article when available.

## License

This repository is released under the MIT License. See `LICENSE`.

## Contribution Guidelines

Contributions should preserve reproducibility. In particular:

- do not hard-code API keys,
- do not add canned LLM outputs to the production pipeline,
- keep prompt templates in `prompts/`,
- keep logs sufficient to reproduce each repair run,
- add or update tests for changes to parsing, ranking, LLM orchestration, patch application, validation, or logging.

## License

All Rights Reserved © 2026 Fatma Gülşah Tan

This repository is publicly available solely to support research transparency and the peer-review process.

No permission is granted to copy, modify, redistribute, or use any part of this repository without prior written permission from the copyright holder.
