#!/usr/bin/env python3
"""Compare a Stryker JSON report to quality/mutation-baseline.json.

Fail when a known file's mutation score drops more than 5 percentage points
or below its recorded break floor. Newly mutated critical files must meet
the module floor until a full run records a per-file baseline.
"""

from __future__ import annotations

import argparse
import json
import math
import sys
from pathlib import Path
from typing import Any


def load_json(path: Path) -> dict[str, Any]:
    if not path.is_file():
        raise SystemExit(f"error: missing {path}")
    data = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(data, dict):
        raise SystemExit(f"error: {path} must contain a JSON object")
    return data


def normalize_path(path: str) -> str:
    return path.replace("\\", "/").lstrip("./")


def file_counts(mutants: list[dict[str, Any]]) -> dict[str, int]:
    counts = {
        "total": 0,
        "killed": 0,
        "survived": 0,
        "noCoverage": 0,
        "timeout": 0,
        "ignored": 0,
        "compileError": 0,
    }
    for mutant in mutants:
        status = str(mutant.get("status") or "")
        if status == "Ignored":
            counts["ignored"] += 1
            continue
        if status == "CompileError":
            counts["compileError"] += 1
            continue
        counts["total"] += 1
        if status == "Killed":
            counts["killed"] += 1
        elif status == "Survived":
            counts["survived"] += 1
        elif status == "NoCoverage":
            counts["noCoverage"] += 1
        elif status == "Timeout":
            counts["timeout"] += 1
        else:
            counts["total"] -= 1
    return counts


def score_from_counts(counts: dict[str, int]) -> float | None:
    scored = counts["killed"] + counts["survived"] + counts["noCoverage"] + counts["timeout"]
    if scored == 0:
        return None
    return 100.0 * counts["killed"] / scored


def iter_report_files(report: dict[str, Any]) -> list[tuple[str, dict[str, Any]]]:
    files = report.get("files")
    if not isinstance(files, dict):
        raise SystemExit("error: mutation report is missing files")
    rows: list[tuple[str, dict[str, Any]]] = []
    for raw_path, payload in files.items():
        if not isinstance(payload, dict):
            continue
        mutants = payload.get("mutants")
        if not isinstance(mutants, list):
            continue
        counts = file_counts(mutants)
        score = score_from_counts(counts)
        rows.append(
            (
                normalize_path(raw_path),
                {**counts, "score": score},
            )
        )
    return rows


def shorten(path: str) -> str:
    for prefix in ("backend/", "frontend/"):
        idx = path.find(prefix)
        if idx >= 0:
            return path[idx:]
    return path


def break_floor(score: float) -> int:
    return max(0, math.floor(score) - 5)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--baseline", type=Path, required=True)
    parser.add_argument(
        "--informational",
        action="store_true",
        help="Print regressions but exit 0",
    )
    parser.add_argument(
        "--write-baseline",
        action="store_true",
        help="Update baseline file scores from the report",
    )
    args = parser.parse_args(argv)

    report = load_json(args.report)
    baseline = load_json(args.baseline)
    raw_floor = baseline.get("moduleFloor", 60)
    module_floor = 60.0 if raw_floor is None else float(raw_floor)
    known = baseline.get("files")
    if not isinstance(known, dict):
        known = {}
        baseline["files"] = known

    failures: list[str] = []
    for path, stats in iter_report_files(report):
        key = shorten(path)
        score = stats["score"]
        if args.write_baseline:
            if score is None:
                continue
            known[key] = {
                "total": stats["total"],
                "killed": stats["killed"],
                "survived": stats["survived"],
                "noCoverage": stats["noCoverage"],
                "timeout": stats["timeout"],
                "ignored": stats["ignored"],
                "score": round(score, 2),
                "breakFloor": break_floor(score),
            }
            continue
        if score is None:
            continue
        recorded = known.get(key)
        if not isinstance(recorded, dict):
            if score + 1e-9 < module_floor:
                failures.append(
                    f"{key} score {score:.2f}% is below module floor {module_floor:g}%"
                )
            continue
        floor = recorded.get("breakFloor")
        if floor is None and "score" in recorded:
            floor = break_floor(float(recorded["score"]))
        if floor is not None and score + 1e-9 < float(floor):
            failures.append(
                f"{key} score {score:.2f}% dropped below break floor {float(floor):g}%"
            )
            continue
        previous = recorded.get("score")
        if previous is not None and score + 1e-9 < float(previous) - 5:
            failures.append(
                f"{key} score {score:.2f}% dropped more than 5pp from {float(previous):g}%"
            )

    if args.write_baseline:
        args.baseline.write_text(json.dumps(baseline, indent=2) + "\n", encoding="utf-8")
        print(f"updated {args.baseline} ({len(known)} files)")
        return 0

    if failures:
        print("mutation baseline check failed:", file=sys.stderr)
        for item in failures:
            print(f"  - {item}", file=sys.stderr)
        if args.informational:
            print("informational: not failing the job", file=sys.stderr)
            return 0
        return 1

    print("mutation baseline check passed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
