# LLM output controls

The implementation uses OpenAI .NET SDK 2.14.0 and Chat Completions with strict
JSON Schema for diagnosis, patch generation, refinement and consistency checks.
Required properties and rejection of extra properties keep outputs structured.
Unsupported models or schemas produce an explicit error; there is no fallback.

| Stage | Default maximum output tokens |
|---|---:|
| DIAGNOSE | 512 |
| PATCH and refinement | 2048 |
| CHECK | 512 |
| EXPLAIN | 384 |

The default per-call ceiling is 2048, the cumulative budget is 16000 tokens,
and at most 20 API attempts are allowed per defect. Transport retries are disabled.
A failed stage can regenerate once, within both budgets. A length-limited output
is always rejected and retained for inspection, even if it resembles valid JSON.
It is never applied as a patch. Unknown usage stops further calls.

`LLM:OutputLimits` controls individual stages in the web application; manifests
use `model.outputLimits`. `MaxTokens` is a hard ceiling. For an explicitly
configured larger-output run, setting `MaxTokens=4096` allows the single patch
length retry to use `PatchLengthRetry=4096`; this differs from the default paper
protocol and is recorded in the run configuration. Invalid JSON does not increase
the limit. RCA, CHECK and explanation retain their initial limits on retry.

Retries retain the original artifact context and compact corrective instructions,
without replaying the incomplete answer. The full selected method is preserved.
Repeated metadata and diagnostic text are removed. Schema constraints bound
explanatory prose without truncating unified diffs.

Logs record stage, attempt, actual response model, finish reason, input/output
usage, elapsed time and classified errors. Failure kinds distinguish invalid JSON,
missing fields, output truncation, repetition and API/configuration errors.
Token usage is supplied by the API; costs are estimates only when the caller
provides matching model pricing. Structural validity does not prove correctness.
