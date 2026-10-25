# Ranking and parser policy (v1)

The unit is a method/function with ID `relative/file::context.signature`. The complete ranking is stored; search uses only its first `TopK` entries. Top-5 evaluation always uses the first five entries of the complete ranking.

## Artifact scores

* Code: arithmetic mean of five binary lexical indicators: boundary sensitivity (`<=`, `>=`, length/size/len/count); member dereferencing (`name.member` or `name->member`); indexed access (`name[expression]`); exceptional control flow (throw/throws/try/catch/except/raise/finally); conditional complexity/comparison (`&&`, `||`, and/or, ==, !=). These conservative proxies do not establish that a defect exists.
* Trace: inverse one-based frame position of the first unambiguous match. File basename, method/class name and optional line interval must agree. An unresolved overload is unmatched; no arbitrary overload receives credit.
* Report: split camelCase, acronym boundaries and underscores, lowercase tokens; TF-IDF cosine between the report and method signature, class context and comments. IDF is `log((1+number of methods)/(1+document frequency))+1`, computed only within the buggy revision. Term frequency is raw occurrence count.
* History: sum `exp(-lambda * daysBeforeDefect)` over distinct mapped commits within H days strictly before the defect timestamp. Default H=365, lambda=0.01/day are explicit new-study choices. Dividing every history score by its common positive maximum would not alter the ordering, so the ranker omits that redundant scaling. Unknown mappings must be omitted, never guessed from the reference fix.

Convert each score vector to descending ranks. Equal raw values occupy average ranks, including all-zero vectors. For N>1, contribution is `(N-r)/(N-1)`; for N=1 it is 1. Default weights are .40/.30/.20/.10. Unavailable artifacts have zero weight and remaining weights are normalized without rounding. All-zero available weights produce a source-rank/signature ordering, not a silently enabled source weight. Equal aggregate scores break on code Borda, then ordinal method ID. Code-ranking ablation also removes code as a tie-breaker.

History JSON is an array of `{ "methodId": "...", "timestamp": "ISO-8601", "commit": "full SHA" }`. A null history means unavailable; an empty array with a known defect time means a known window with no mapped changes. Stack-trace evidence is available only if frames were parsed, not merely because arbitrary compiler output exists.

## Extraction limitations

Comments and quoted strings are masked before declaration/boundary extraction; their original text remains available as artifact context. Ambiguous overlapping line scopes are rejected by validation. The existing language-specific regex extractors are retained rather than falsely described as complete AST parsers. They recognize common brace-bodied Java/C/C# methods and simple Python `def` functions. They do not guarantee full handling of constructors, annotations, nested classes, macros, multiline Python signatures, decorators, async Python or expression-bodied C#. No file-scope fallback is counted as a method. Inspect candidate IDs and intervals before running expensive experiments. Complex unsupported syntax requires a parser upgrade or a transparently recorded exclusion, not guessed labels. These limitations are material to interpretation of Top-1/Top-5.
