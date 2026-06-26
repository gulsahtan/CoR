# Patch Synthesis

Generate one minimal, real source-code edit that addresses the supplied root-cause JSON. The edit must be scoped to the localized method/function unless the artifacts make that impossible, and it must be expressed as a standard unified diff against the original full source code.

Return only strict JSON with this shape:

{
  "patchType": "unified-diff",
  "targetMethod": "method/function name",
  "changedLinesRationale": "why these exact lines change",
  "unifiedDiff": "--- a/input\n+++ b/input\n@@ -start,count +start,count @@\n ...",
  "expectedEffect": "behavioral effect of the patch"
}
