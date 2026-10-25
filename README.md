
# Chain-of-Repair (CoR)

**An Artifact-Aware Multi-Stage Reasoning Framework for Fault Localization and Program Repair with Large Language Models**

Chain-of-Repair (CoR) is a research-oriented automated fault localization and program repair framework implemented in **C# 13 and .NET 9**, with an ASP.NET Core web application and a command-line interface.

CoR integrates artifact-aware method ranking, structured LLM reasoning, diagnosis-conditioned patch generation, conservative validation, bounded refinement and developer-oriented explanations.

The framework operates **without executing benchmark tests during fault localization, diagnosis, patch generation or candidate validation**. Benchmark tests are used separately, after repair termination, to assess retrospective correctness.

> **Important:** A framework-plausible patch passes CoR's configured validation checks but is not necessarily functionally correct. Retrospective test passing provides additional behavioral evidence without establishing complete functional correctness.

## Table of Contents

1. [Overview](#overview)
2. [System Architecture](#system-architecture)
3. [Artifact-Aware Fault Localization](#artifact-aware-fault-localization)
4. [Multi-Stage Reasoning](#multi-stage-reasoning)
5. [Patch Validation and Refinement](#patch-validation-and-refinement)
6. [Repository Structure](#repository-structure)
7. [Requirements](#requirements)
8. [Installation](#installation)
9. [Running the Web Application](#running-the-web-application)
10. [Running the CLI](#running-the-cli)
11. [Running Project Tests](#running-project-tests)
12. [Experimental Evaluation](#experimental-evaluation)
13. [Reproducibility and Experiment Artifacts](#reproducibility-and-experiment-artifacts)
14. [Logging and Outputs](#logging-and-outputs)
15. [Limitations](#limitations)
16. [Data Availability and Citation](#data-availability-and-citation)
17. [License](#license)

---

## Overview

CoR addresses fault localization and automated program repair in software environments where executable test suites may be incomplete, outdated, expensive or unavailable during the repair process.

The framework combines four categories of software artifacts:

- Source-code context
- Stack traces and permitted diagnostic information
- Bug reports and natural-language defect descriptions
- Revision history, when available

These artifacts support method-level fault localization and guide subsequent repair decisions.

CoR follows four primary reasoning stages:

**DIAGNOSE → PATCH → CHECK → EXPLAIN**

Candidate patches are evaluated using compilation or syntax checks, differential static analysis, local semantic constraints and diagnosis–patch consistency assessment.

Rejected candidates may undergo bounded refinement.

The framework distinguishes between:

- **Framework-plausible repair:** A patch satisfying the configured non-test acceptance criteria.
- **Retrospectively test-passing repair:** A final patch passing benchmark-provided tests after the repair process terminates.

CoR does not require task-specific model training or fine-tuning.

---

## System Architecture

The framework consists of three interconnected modules:

1. Localization Module
2. Reasoning Module
3. Validation Module

### End-to-End Workflow

```text
Buggy Program Revision
         |
         v
Available Software Artifacts
(Source Code, Stack Trace, Bug Report, History)
         |
         v
Candidate Method Extraction
         |
         v
Artifact-Specific Scoring
         |
         v
Weighted Borda Rank Aggregation
         |
         v
Top-K Suspicious Methods
         |
         v
+-----------------------------+
| DIAGNOSE                    |
| Structured Root-Cause       |
| Analysis                    |
+-----------------------------+
         |
         v
+-----------------------------+
| PATCH                       |
| Diagnosis-Conditioned       |
| Patch Generation            |
+-----------------------------+
         |
         v
+-----------------------------+
| CHECK                       |
| Diagnosis-Patch Consistency |
+-----------------------------+
         |
         v
+-----------------------------+
| VALIDATION                  |
| Patch Integrity             |
| Compilation / Syntax        |
| Differential Static Checks  |
| Local Semantic Constraints  |
+-----------------------------+
         |
         v
   Candidate Accepted?
      /          \
    Yes           No
     |             |
     v             v
  EXPLAIN      Refinement
     |         (Max. 3)
     |             |
     |       Revalidate
     v
Framework-Plausible Patch
         |
         v
Repair Process Terminates
         |
         v
Retrospective Benchmark Testing
         |
         v
Final Evaluation Results
```

### Execution Protocol

The experimental protocol specifies:

| Parameter | Value |
|---|---|
| Maximum selected methods | Top-K = 5 |
| Initial patch attempts | 1 per selected method |
| Maximum refinements | 3 per selected method |
| Maximum patch evaluations | 4 per selected method |
| Maximum LLM calls | 20 per defect |
| Maximum total inference tokens | 16,000 per defect |
| Maximum output tokens | 2,048 per model call |
| Temperature | 0.2 |
| LLM family | GPT-4o |

The model-call and token limits are cumulative across the repair operations for each defect.

The per-method refinement budget and global inference budget are distinct constraints. The effective termination behavior depends on both.

---

## Artifact-Aware Fault Localization

CoR performs fault localization at the method level.

Let:

- `M` denote the candidate methods.
- `A` denote the available artifact types.
- `s_a(m)` denote the artifact-specific suspiciousness score.
- `r_a(m)` denote the descending rank of method `m` under artifact `a`.

### Source-Code Evidence

Source-code suspiciousness is computed from five binary static indicators:

1. Boundary-condition sensitivity
2. Null-sensitive dereferencing
3. Potentially unsafe index access
4. Exceptional control-flow handling
5. Conditional-logic inconsistency

The source-code score is the arithmetic mean of these indicators.

### Stack-Trace Evidence

For a method appearing at stack-frame position `j`:

```text
trace_score(m) = 1 / j
```

Unmatched methods receive zero.

Method matching uses available class and signature information to resolve ambiguous references.

### Bug-Report Evidence

Bug-report relevance is calculated using TF–IDF cosine similarity between normalized bug-report text and method-related source context.

Preprocessing includes:

- Lowercase normalization
- CamelCase segmentation
- Underscore-separated identifier tokenization
- Method signature extraction
- Comment and surrounding class-context extraction

Vocabulary and inverse-document-frequency statistics are computed within each buggy revision.

### Revision-History Evidence

When reliable revision-history information is available, method-level change history is represented using exponentially decayed historical modifications.

```text
history_score(m) =
    sum(exp(-lambda * delta_t))
```

The score is normalized relative to the maximum nonzero history score among candidate methods.

History evidence is excluded when commit information cannot be reliably mapped to methods.

### Weighted Borda Aggregation

For `N > 1`, normalized Borda contribution is:

```text
phi(r) = (N - r) / (N - 1)
```

For a single candidate method:

```text
phi(1) = 1
```

The final method score is:

```text
S(m) = sum(w_normalized[a] * phi(r_a(m)))
```

Default artifact weights:

| Artifact | Weight |
|---|---:|
| Source code | 0.40 |
| Stack trace | 0.30 |
| Bug report | 0.20 |
| Revision history | 0.10 |

Unavailable artifacts are excluded and the remaining weights are normalized to sum to one.

Equal artifact scores receive the average occupied rank. Equal aggregate scores are resolved by source-code rank and then by lexicographic fully qualified method signature.

The default weights are heuristic priorities rather than learned reliability estimates.

---

## Multi-Stage Reasoning

The Reasoning Module uses structured LLM operations to connect fault localization, diagnosis, patch synthesis and explanation generation.

### Stage 1: DIAGNOSE

Inputs:

- Selected suspicious method
- Method signature and source context
- Available diagnostic artifacts
- Normalized failure description

Outputs:

- Suspected fault category
- Relevant statement or code region
- Inferred defect mechanism
- Supporting evidence
- Confidence information

The resulting structured diagnosis is passed to patch synthesis.

### Stage 2: PATCH

Inputs:

- Original method
- Structured diagnosis
- Applicable scope constraints
- Interface-preservation requirements

Outputs:

- Candidate unified diff
- Edited method
- Patch rationale
- Expected correction mechanism

The generated modification is restricted to the permitted repair scope.

### Stage 3: CHECK

Inputs:

- Original method
- Diagnosis
- Proposed patch
- Supporting artifacts

Outputs:

- Diagnosis–patch consistency judgment
- Fault–patch alignment judgment
- Constraint violations
- Refinement feedback

The LLM-generated judgment is combined with deterministic validation.

A positive consistency judgment alone does not establish functional correctness.

### Stage 4: EXPLAIN

The EXPLAIN operation runs only after candidate acceptance.

It produces a developer-oriented explanation containing:

- Suspected root cause
- Applied code modification
- Supporting evidence
- Validation summary
- Expected effect of the repair

Explanation generation does not override failed validation decisions.

### Prompt Templates

The previously documented runtime prompt templates are located in:

```text
prompts/
  root_cause.md
  patch_synthesis.md
  self_consistency.md
  refinement.md
  explanation.md
```

The conceptual DIAGNOSE, PATCH, CHECK and EXPLAIN stages correspond to the appropriate runtime prompt templates.

---

## Patch Validation and Refinement

CoR validates generated patches without executing benchmark tests during the repair process.

### Patch Integrity and Scope

Candidate patches are checked for:

- Valid unified diff structure
- Successful patch application
- Modifications within the permitted method scope
- Preservation of required interfaces
- Absence of unrelated changes

### Compilation and Syntax Validation

Applicable language-specific tools include:

| Language | Validation |
|---|---|
| Java | Project compilation |
| Python | Python syntax compilation |
| C | Compiler or syntax checks |
| C# | Configured build or syntax checks |

### Differential Static Analysis

The experimental validation protocol specifies:

- Java: SpotBugs
- C: clang-tidy
- Python: syntax compilation with applicable additional checks

Original and patched revisions are analyzed using identical configurations.

Pre-existing warnings do not automatically cause rejection. Newly introduced or worsened findings may trigger rejection according to the configured validation policy.

### Lightweight Semantic Constraints

The framework checks for issues such as:

- Unrelated method modifications
- Unsupported interface changes
- Unresolved symbols
- Null-safety violations
- Boundary-condition violations
- Control-flow inconsistencies

### Iterative Refinement

When a candidate fails validation:

1. Validation feedback is collected.
2. The original diagnosis is preserved.
3. The previous patch and rejection reasons are supplied to PATCH.
4. A revised candidate is generated.
5. The complete applicable validation procedure is repeated.

Up to three refinement iterations are permitted per selected method.

If the candidate remains unacceptable, the framework may continue with the next ranked method, subject to the global inference budget.

### Test Isolation

Benchmark-provided test suites and reference patches must not influence:

- Fault localization
- Diagnosis
- Patch generation
- CHECK
- Candidate selection
- Refinement

Benchmark tests are executed only after repair termination.

---

## Repository Structure

The previously documented project structure is:

```text
ChainOfRepair.sln
|
+-- src/
|   +-- ChainOfRepair.Core/
|   |   +-- Parsing/
|   |   |   +-- ArtifactParsers.cs
|   |   +-- Ranking/
|   |   |   +-- WeightedBordaRanker.cs
|   |   +-- Reasoning/
|   |   |   +-- LlmReasoning.cs
|   |   +-- Validation/
|   |   |   +-- PatchApplicationService.cs
|   |   |   +-- ValidationEngine.cs
|   |   +-- Pipeline/
|   |   |   +-- RepairPipeline.cs
|   |   +-- Models/
|   |       +-- CoRModels.cs
|   |
|   +-- ChainOfRepair.Web/
|   +-- ChainOfRepair.Cli/
|
+-- tests/
|   +-- ChainOfRepair.Tests/
|
+-- prompts/
+-- docs/
|   +-- datasets.md
+-- sample_inputs/
+-- sample_outputs/
+-- logs/
|   +-- runs/
+-- LICENSE
```

The Core project contains the main localization, reasoning, patching, validation and orchestration components.

The Web project provides the browser-based interface.

The CLI project supports command-line execution and batch-oriented workflows.

---

## Requirements

### Required Software

- .NET 9 SDK
- OpenAI API access
- Internet connectivity for live LLM calls

### Optional Language Tools

- Java Development Kit (`javac`)
- Python (`python` or `python3`)
- GCC or Clang for C
- SpotBugs
- clang-tidy
- Additional project-specific build dependencies

Static-analysis availability and configuration may affect the validation outcome.

### Computing Environment

The manuscript reports the following environment:

| Component | Specification |
|---|---|
| Operating system | Windows 10, 64-bit |
| Processor | Intel Core i7-11700F |
| CPU cores / threads | 8 / 16 |
| Memory | 32 GB |
| GPU | NVIDIA GeForce RTX 3070, 8 GB |
| Runtime | .NET 9 |
| Programming language | C# 13 |
| Web framework | ASP.NET Core |
| LLM integration | OpenAI API |

LLM inference is performed through the OpenAI API.

Local resources are used for orchestration, parsing, compilation and applicable static-analysis operations.

---

## Installation

### 1. Clone the Repository

```bash
git clone https://github.com/gulsahtan/CoR.git
cd CoR
```

### 2. Restore Dependencies

```bash
dotnet restore ChainOfRepair.sln
```

### 3. Build the Solution

```bash
dotnet build ChainOfRepair.sln
```

### 4. Configure the OpenAI API Key

For Windows PowerShell:

```powershell
$env:OPENAI_API_KEY = "your-openai-api-key"
```

To save the variable persistently:

```powershell
setx OPENAI_API_KEY "your-openai-api-key"
```

Restart the terminal after using `setx`.

Never commit API keys or credentials to the repository.

### 5. Optional Environment Variables

```text
OPENAI_API_KEY
COR_OPENAI_MODEL
COR_JAVA_COMPILER
COR_PYTHON
COR_C_COMPILER
COR_CSHARP_BUILD
```

These variables configure model selection and language-specific tooling.

The previously documented default model alias is `gpt-4o`.

For experimental reproducibility, use and record an exact model snapshot rather than relying only on a moving alias.

---

## Running the Web Application

Start the application:

```bash
dotnet run --project src/ChainOfRepair.Web/ChainOfRepair.Web.csproj
```

Open the local address displayed by ASP.NET Core.

The web interface supports input fields for:

- Programming language
- Source code
- Bug description
- Optional diagnostic information
- Top-K localization depth

The general-purpose application can accept diagnostic or failure text. For benchmark experiments, only artifacts permitted by the corresponding experimental protocol should be supplied.

### Repair Execution

After submitting a repair request, the application executes the configured localization and repair pipeline.

The interface can display:

- Ranked suspicious methods
- Root-cause analysis
- Candidate patch
- Consistency assessment
- Validation results
- Refinement attempts
- Final patched source code
- Unified diff
- Developer explanation
- Run-log location

An accepted patch should be interpreted as framework-plausible unless independently evaluated for behavioral correctness.

---

## Running the CLI

The previously documented command-line invocation is:

```bash
dotnet run --project src/ChainOfRepair.Cli/ChainOfRepair.Cli.csproj -- --input sample_inputs --output sample_outputs --language Java --topk 5
```

### Input Format

The documented sample-case structure is:

```text
case-id/
  source.java
  bug.txt
  failing.txt
```

The source filename may use a supported language extension such as `.java`, `.py` or `.c`.

`bug.txt` contains an optional natural-language defect description.

`failing.txt` contains optional diagnostic information. It must not contain benchmark test-oracle information when executing the test-constrained research protocol.

### CLI Outputs

The documented CLI outputs include:

- Per-case JSON results
- `summary.csv`
- Detailed run logs
- Generated patches
- Validation outcomes

The sample CLI command is intended for application testing. Reproducing the complete benchmark evaluation requires the corresponding benchmark manifests, experimental configurations and evaluation scripts.

---

## Running Project Tests

Execute the documented project test runner:

```bash
dotnet run --project tests/ChainOfRepair.Tests/ChainOfRepair.Tests.csproj
```

The test suite is intended to verify implementation behavior, including:

- Parsing and method extraction
- Artifact ranking
- Prompt construction
- LLM-client configuration
- Missing API-key handling
- Patch application
- Validation logic
- Refinement limits
- Run logging

These implementation tests are separate from retrospective benchmark correctness evaluation.

---

## Experimental Evaluation

The accompanying manuscript evaluates CoR on three established program repair benchmarks.

### Benchmark Datasets

| Dataset | Language | Defects |
|---|---|---:|
| Defects4J v1.2 | Java | 150 |
| QuixBugs | Java / Python | 40 |
| IntroClass | C | 150 |
| **Total** | | **340** |

Dataset sources:

- Defects4J: https://github.com/rjust/defects4j
- QuixBugs: https://github.com/jkoppel/QuixBugs
- IntroClass: https://github.com/ProgramRepair/IntroClass

The 150 Defects4J defects are selected using a fixed experimental manifest.

### Fault Localization and Repair Results

The manuscript reports:

| Dataset | Top-1 | Top-5 | Framework-Plausible | Test-Passing |
|---|---:|---:|---:|---:|
| Defects4J | 69/150 | 107/150 | 47/150 | 27/150 |
| QuixBugs | 13/40 | 23/40 | 22/40 | 13/40 |
| IntroClass | 87/150 | 120/150 | 99/150 | 72/150 |
| **Total** | **169/340** | **250/340** | **168/340** | **112/340** |

Overall reported results:

- Top-1 localization accuracy: **49.7%**
- Top-5 localization accuracy: **73.5%**
- Framework-plausible repair rate: **49.4%**
- Retrospective test-passing repair rate: **32.9%**

These are manuscript-reported results and should be reproduced from the corresponding defect-level experiment records.

### Direct Prompting Baseline

CoR is compared against direct GPT-4o prompting under the same maximum inference budgets and benchmark conditions.

| Metric | CoR | Direct GPT-4o |
|---|---:|---:|
| Defects | 340 | 340 |
| Top-1 localization | 169 | 169 |
| Top-5 localization | 250 | 250 |
| Test-passing repairs | 112 | 78 |
| Mean tokens per defect | 8,400 | 4,600 |

Both configurations use the same maximum resource allocation, but their actual token consumption differs.

### Sensitivity Analysis

The manuscript describes a separate 30-defect calibration experiment covering:

- Alternative artifact weights
- Top-K localization settings
- Refinement budgets

The calibration sample is specified as distinct from the main evaluation set.

### Ablation Experiments

The experimental design investigates the contribution of:

- Structured reasoning
- Validation
- Individual artifact sources
- Individual validation mechanisms

### Transformation Robustness

The manuscript reports paired original/transformed evaluations on 60 defects.

| Configuration | Test-Passing Repairs |
|---|---:|
| Original | 20/60 |
| Transformed | 17/60 |

Transformations include identifier renaming, formatting and comment normalization and applicable semantics-preserving restructuring.

These experiments measure sensitivity to selected program transformations. They do not establish the absence of benchmark exposure during model training.

### Runtime and Inference Cost

| Configuration | Median Latency | Mean Calls/Defect | Mean Tokens/Defect |
|---|---|---:|---:|
| Full CoR | 42 s (IQR 28–71) | 8.2 | 8,400 |
| Direct GPT-4o | 25 s (IQR 16–43) | 5.1 | 4,600 |
| Top-K = 10 | 57 s (IQR 35–96) | 11.4 | 11,900 |

The multi-stage approach incurs additional inference cost compared with direct prompting.

### Developer Study

The manuscript also reports an exploratory developer study involving 10 participants.

Reported outcomes include:

- Explanation clarity: 84.0%
- Debugging time reduction: 22.0%
- Developer trust rating: 4.1/5

These findings describe the evaluated study conditions and should not be interpreted as population-level proof of developer productivity improvements.

---

## Reproducibility and Experiment Artifacts

Scientific replication requires versioned experiment configurations, benchmark identifiers, raw results and aggregation procedures.

A recommended organization is:

```text
experiments/
  manifests/
  configs/
  runners/
  transformations/
  results/
    raw/
    aggregated/
  analysis/
  environments/
  examples/
```

This structure is a recommended organization for the experimental materials, not a claim that every directory is already present in the current repository.

### Experiment Records

Each experiment should record:

- Benchmark name and version
- Defect identifier
- Source revision or hash
- Available artifact types
- Artifact-specific scores
- Ranked candidate methods
- Prompt template identifiers
- Model snapshot
- LLM call counts
- Input and output token counts
- Generated patches
- Validation decisions
- Rejection reasons
- Refinement attempts
- Execution time
- Final framework status
- Retrospective test outcome

### Result Reproduction

The complete replication package should provide a traceable mapping between:

1. Experimental configuration
2. Input defect manifest
3. Raw defect-level outputs
4. Aggregation procedure
5. Published tables and metrics

The GitHub release and Zenodo archive should identify the same code and experimental configuration.

### Worked Example

A complete repair trace should demonstrate:

```text
Input Defect
    |
    v
Artifact Extraction
    |
    v
Method Ranking
    |
    v
Structured Diagnosis
    |
    v
Patch Generation
    |
    v
Consistency Assessment
    |
    v
Compilation and Static Validation
    |
    v
Final Patch
    |
    v
Retrospective Test Evaluation
```

Retrospective test results must not be fed back into the repair process.

---

## Logging and Outputs

The previously documented logging directory is:

```text
logs/runs/{runId}/
```

A run may produce files such as:

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

Log availability depends on which stages execute.

Sensitive values, including API keys and credentials, must be excluded from publicly shared logs.

---

## Limitations

CoR has several important limitations.

- Framework-plausible patches are not guaranteed to be functionally correct.
- Benchmark test passing does not prove complete semantic correctness.
- Static-analysis coverage varies across programming languages and tool configurations.
- Artifact availability and quality influence localization performance.
- Repair performance depends on model configuration, prompts, inference budgets and benchmark characteristics.
- Public benchmark exposure may influence LLM behavior.
- Multi-stage inference increases computational cost and latency.
- Results obtained from selected benchmarks may not generalize to large industrial systems.

---

## Data Availability and Citation

**Source Code**

https://github.com/gulsahtan/CoR

**Zenodo Archive**

https://doi.org/10.5281/zenodo.20774174

**Benchmark Sources**

- Defects4J: https://github.com/rjust/defects4j
- QuixBugs: https://github.com/jkoppel/QuixBugs
- IntroClass: https://github.com/ProgramRepair/IntroClass

When using CoR in academic research, cite the accompanying Chain-of-Repair manuscript and the original benchmark publications.

For reproducible experiments, reference the exact repository commit and archived release.

---

## License

See the repository `LICENSE` file for the applicable licensing terms.
