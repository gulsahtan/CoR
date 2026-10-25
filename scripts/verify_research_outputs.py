"""Reconcile exported observations with measured records, without API calls."""
import csv
import json
import sys
from decimal import Decimal
from pathlib import Path


def verify(folder):
    folder = Path(folder)
    records = [json.loads(p.read_text(encoding="utf-8-sig")) for p in folder.glob("*.record.json")]
    assert records, "No measured records"
    tables = {}
    for name in ("experiments", "token_usage", "api_costs", "fault_localization", "root_cause_results", "patch_results", "summary_results"):
        with (folder / (name + ".csv")).open(encoding="utf-8-sig", newline="") as f:
            tables[name] = list(csv.DictReader(f))
    json.loads((folder / "experiment_config.json").read_text(encoding="utf-8-sig"))
    calls = [c for r in records for c in r["result"]["modelCalls"]]
    assert len(tables["experiments"]) == len(records)
    assert len(tables["token_usage"]) == len(calls) == len(tables["api_costs"])
    assert {r["run_id"] for r in tables["experiments"]} == {r["runId"] for r in records}
    for column, key in (("input_tokens", "inputTokens"), ("output_tokens", "outputTokens"), ("total_tokens", "totalTokens")):
        assert sum(int(r[column]) for r in tables["token_usage"] if r[column]) == sum(c[key] for c in calls if c[key] is not None)
        assert sum(not r[column] for r in tables["token_usage"]) == sum(c[key] is None for c in calls)
    assert sum(Decimal(r["estimated_cost_usd"]) for r in tables["api_costs"] if r["estimated_cost_usd"]) == sum(Decimal(str(c["estimatedCostUsd"])) for c in calls if c["estimatedCostUsd"] is not None)
    by_id = {r["runId"]: r for r in records}
    for row in tables["experiments"]:
        record = by_id[row["run_id"]]
        for column, key in (("root_cause_correct", "rootCauseCorrect"), ("independently_verified", "independentlyVerified"), ("top1", "top1")):
            if record[key] is None:
                assert row[column] == "", f"Missing {column} replaced by a value"
        if record["tests"] and record["tests"]["exitCode"] == 1:
            assert row["benchmark_tests_passed"].lower() == "false", "Failed test omitted"
    print(f"Verified {len(records)} records, {len(calls)} API calls, seven CSV files and configuration.")


if __name__ == "__main__":
    verify(sys.argv[1])
