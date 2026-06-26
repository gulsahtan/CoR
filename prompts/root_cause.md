# Root-Cause Analysis

Analyze the localized method/function and all supplied artifacts. Use the failing output, stack trace frames, compiler diagnostics, optional bug description, source context, and ranked method metadata as evidence.

Return only strict JSON with this shape:

{
  "faultCategory": "short category name",
  "rootCause": "specific causal explanation",
  "evidence": ["artifact-grounded evidence item"],
  "localizedMethod": "context.method",
  "confidence": 0.0
}
