# Patch Refinement

The previous patch failed validation. Use the original source code, previous patch, patched code, validation failures, original root-cause JSON, and localized method/function boundary to produce a smaller corrected patch.

Do not change the root-cause diagnosis. Do not introduce unrelated refactoring. Do not return prose instead of a patch.

Return only strict JSON with this shape:

{
  "patchType": "unified-diff",
  "targetMethod": "method/function name",
  "changedLinesRationale": "how the corrected patch fixes the validation failure",
  "unifiedDiff": "--- a/input\n+++ b/input\n@@ -start,count +start,count @@\n ...",
  "expectedEffect": "behavioral effect of the refined patch"
}
