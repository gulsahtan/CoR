# Local configuration

Follow the root README for installation and startup. Run commands from the
repository root. No API credential or experimental output is distributed.

## OpenAI

The application reads the key from `OPENAI_API_KEY` in its process environment.
To enter it without storing the value in PowerShell command history:

```powershell
$corSecret = Read-Host 'OpenAI API key' -AsSecureString
$env:OPENAI_API_KEY = [System.Net.NetworkCredential]::new('', $corSecret).Password
$env:COR_OPENAI_MODEL = 'gpt-4o'
dotnet run --project src/ChainOfRepair.Web
```

`COR_OPENAI_MODEL` overrides the configured model for Web and the sample CLI.
Experiment manifests specify their own model. The actual returned model is logged.
The Web application also reads optional
`src/ChainOfRepair.Web/appsettings.Local.json`, which is ignored by Git and Docker.
Environment keys such as `LLM__MaxCalls` override that local configuration.
The production application always uses the real OpenAI client.

## Compilation and differential static analysis

The default compiler executables are `javac`, `python` and `clang` for Java,
Python and C. Override them through `COR_JAVA_COMPILER`, `COR_PYTHON` and
`COR_C_COMPILER`. For C#, supply a `Validation.Build` command for the project,
or set `COR_CSHARP_BUILD` to a build-wrapper executable available on PATH.
Commands execute without shell interpolation, in isolated project copies.

For full validation, configure `Validation.StaticAnalysis`. The command must run
a real analyzer and print a JSON array of findings. The provided
`scripts/static_analysis.py` supports SpotBugs, clang-tidy and Ruff; see its
`--help` and [experiments.md](experiments.md). Missing compilers or analyzers are
reported explicitly and never treated as passed checks.

A concrete Python setup, after installing Python and Ruff:

```powershell
python -m pip install ruff
$corPython = (Get-Command python).Source
$corAnalyzer = (Resolve-Path scripts/static_analysis.py).Path
$corValidation = @{
    Build = @{ Executable = $corPython; Arguments = @('-m', 'py_compile', '{file}'); TimeoutSeconds = 120 }
    StaticAnalysis = @{ Executable = $corPython; Arguments = @($corAnalyzer, 'ruff', '--', '.'); TimeoutSeconds = 120 }
}
@{ Validation = $corValidation } | ConvertTo-Json -Depth 6 | Set-Content src/ChainOfRepair.Web/appsettings.Local.json
$corValidation | ConvertTo-Json -Depth 6 | Set-Content validation.Local.json
```

Use the CLI's `--validation validation.Local.json` option for the same checks.
For Java projects, configure their actual build command and compiled classpath,
then the SpotBugs adapter; single-file compilation uses the submitted filename.
For C, configure compiler flags and the clang-tidy check set required by the project.
Benchmark tests are not validation commands; they belong in the separate
`retrospectiveTests` manifest field.

## Outputs

Logs and result directories are created when a user runs the application.
`logs/`, `sample_outputs/`, `experiments/local/` and `experiments/results/` are
ignored by Git. They can contain submitted source code and diagnostic text;
keep them local unless intentionally sharing a run. Generated output is not
part of the source distribution.
