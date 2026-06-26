# Self-Consistency Check

Check whether the proposed patch is consistent with the root-cause JSON, local source context, patch application result, and method boundary. Identify contradictions, scope drift, unrelated refactoring, and likely validation problems.

Return only strict JSON with this shape:

{
  "passed": true,
  "concerns": [],
  "causalAlignment": "assessment grounded in the artifacts",
  "scopeAssessment": "assessment of whether changes stay inside the localized method/function"
}
