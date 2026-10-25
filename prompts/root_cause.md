Identify the immediate cause of the observed failure in the selected method.
Name the precise language/runtime operation that fails and its triggering condition,
not merely a restatement of the exception.
For Java, check implicit boxing/unboxing, dereferences, indexing and arithmetic;
explicitly name the operation responsible when the code provides that evidence.
Return only the required JSON object. rootCause: one sentence, at most 50 words.
evidence: 1-3 short observations quoting an expression, exception or source line.
faultCategory: a short label. localizedMethod: exactly the supplied Context + "." + Name (not Id, signature or file path).
confidence: 0 to 1; lower it when evidence is insufficient.
Distinguish the observed failure from other possible defects. Do not invent inputs,
execution steps or test outcomes. No repair code or general explanation.
