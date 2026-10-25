Generate the smallest complete unified diff fixing the diagnosed failure inside the selected method.
Preserve omitted code and avoid unrelated refactoring. Return the required JSON;
use patchType "unified-diff" and one short sentence per rationale/effect.
For a one-line replacement by two lines, the hunk structure is:
@@ -N,1 +N,2 @@
-<exact original line including indentation>
+<first replacement line including indentation>
+<second replacement line including indentation>
Substitute actual line numbers and source; never output placeholders.
