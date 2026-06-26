# Chain-of-Repair (CoR)

Chain-of-Repair is a reproducible ASP.NET Core and CLI implementation of an artifact-aware, multi-stage debugging and repair framework for Java, Python, C, and C# source code.

## System Overview

CoR accepts source code, failing test or compile output, an optional bug report, language selection, and Top-K localization depth. It performs:

1. Input preprocessing and artifact extraction.
2. Weighted Borda Count method/function ranking.
3. Four-stage LLM orchestration: root-cause inference, patch synthesis, self-consistency checking, and developer explanation.
4. Validation without running tests during repair generation.
5. Up to three refinement iterations.
6. JSON experiment logging for reproducibility.

## Installation

Prerequisites:

- .NET 9 SDK, or .NET 8+ with project target adjusted if needed.
- Optional compilers on `PATH`: `javac`, `python`, `gcc` or `clang`.
- Optional `OPENAI_API_KEY` for live OpenAI SDK calls.

Restore and build:

```powershell
dotnet restore ChainOfRepair.sln
dotnet build ChainOfRepair.sln
```

## Environment Variables

- `OPENAI_API_KEY`: OpenAI API key. If absent, deterministic offline fallback responses are used.
- `COR_OPENAI_MODEL`: model name, default `gpt-4o`.
- `COR_JAVA_COMPILER`: path to `javac`.
- `COR_PYTHON`: path to `python` or `python3`.
- `COR_C_COMPILER`: path to `gcc` or `clang`.
- `COR_CSHARP_BUILD`: optional path to a C# syntax/build wrapper.

## Running the Web App

```powershell
dotnet run --project src/ChainOfRepair.Web/ChainOfRepair.Web.csproj
```

Open the shown local URL. The main page contains source, failing output, bug description, language, and Top-K controls. The Diagnostics page reports OS, CPU architecture, .NET version, dependency versions, compiler configuration, model settings, experiment logs, preprocessing logs, validation logs, and dataset references.

## Running the CLI

```powershell
dotnet run --project src/ChainOfRepair.Cli/ChainOfRepair.Cli.csproj -- --input sample_inputs --output sample_outputs --language Java --topk 5
```

Expected outputs:

- One JSON file per benchmark case.
- `summary.csv`.
- Timestamped JSON logs in the configured output log folder.

Benchmark case layout:

```text
sample_inputs/
  java-null-division/
    source.java
    failing.txt
    bug.txt
```

## Methodology

Ranking uses Weighted Borda Count:

```text
S_total(l) = Sum(w_i * phi(R_i(l)))
```

Default artifact weights are source-code context `0.40`, stack trace/failing output `0.30`, bug report `0.20`, and revision history placeholder `0.10`. Missing artifact weights are redistributed across present artifacts.

Validation applies:

- Rule A: compilation or syntax filtering through configured tools, with logged fallback mode when unavailable.
- Rule B: differential static analysis heuristics for newly introduced issues.
- Rule C: fault-patch causal alignment with localized method boundaries.

## Code Structure

- `src/ChainOfRepair.Core`: parsers, ranking, LLM orchestration, validation, diagnostics, pipeline.
- `src/ChainOfRepair.Web`: Razor Pages web UI and dashboard.
- `src/ChainOfRepair.Cli`: benchmark batch runner.
- `tests/ChainOfRepair.Tests`: dependency-free unit-style test runner.
- `docs`: dataset references and replication notes.
- `prompts`: prompt templates.
- `sample_inputs`: runnable benchmark-style examples.
- `sample_outputs`: generated outputs.
- `logs`: timestamped experiment logs.

## Tests

```powershell
dotnet run --project tests/ChainOfRepair.Tests/ChainOfRepair.Tests.csproj
```

The test runner covers parser behavior, stack trace extraction, Borda ranking, weight normalization, causal alignment validation, refinement budget enforcement, and JSON log generation.

## Dataset Information

Official dataset links are documented in `docs/datasets.md`:

- Defects4J
- QuixBugs
- IntroClass

## Citation

If you use this replication package, cite the accompanying AI Application article and the datasets/tools used in your benchmark configuration.

## License

MIT License. See `LICENSE`.
