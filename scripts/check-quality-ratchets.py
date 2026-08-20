#!/usr/bin/env python3
"""Fail when coverage floors drop or lag actual coverage by 5pp or more.

Reads frontend/coverage-thresholds.json and coverage/coverage-summary.json.
Missing files or metrics are failures. An optional exceptions file (owned by
issue #855) can temporarily allow a listed decrease until expiresOn.
"""

from __future__ import annotations

import argparse
import json
import math
import sys
from datetime import date
from pathlib import Path
from typing import Any


METRICS = ("branches", "functions", "lines", "statements")


def load_json(path: Path) -> dict[str, Any]:
    if not path.is_file():
        raise SystemExit(f"error: missing {path}")
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as exc:
        raise SystemExit(f"error: invalid JSON in {path}: {exc}") from exc
    if not isinstance(data, dict):
        raise SystemExit(f"error: {path} must contain a JSON object")
    return data


def metric_pct(summary: dict[str, Any], metric: str) -> float:
    block = summary.get("total", {}).get(metric)
    if not isinstance(block, dict) or "pct" not in block:
        raise SystemExit(f"error: coverage summary is missing total.{metric}.pct")
    try:
        return float(block["pct"])
    except (TypeError, ValueError) as exc:
        raise SystemExit(f"error: coverage summary total.{metric}.pct is not a number") from exc


def glob_line_pct(summary: dict[str, Any], needle: str) -> float | None:
    covered = 0
    total = 0
    for path, data in summary.items():
        if path == "total" or not isinstance(data, dict):
            continue
        normalized = path.replace("\\", "/")
        if needle not in normalized:
            continue
        lines = data.get("lines")
        if not isinstance(lines, dict) or "covered" not in lines or "total" not in lines:
            raise SystemExit(f"error: coverage summary is missing lines totals for {path}")
        covered += int(lines["covered"])
        total += int(lines["total"])
    if total == 0:
        return None
    return 100.0 * covered / total


def suggested_floor(actual: float) -> int:
    return max(0, math.floor(actual) - 2)


def parse_exceptions(path: Path | None, today: date) -> set[tuple[str, str]]:
    if path is None:
        return set()
    data = load_json(path)
    allowed: set[tuple[str, str]] = set()
    for raw in data.get("exceptions", []):
        expires_on = date.fromisoformat(str(raw["expiresOn"]))
        if expires_on < today:
            continue
        allowed.add((str(raw["scope"]).strip(), str(raw["metric"]).strip()))
    return allowed


def compare_thresholds(
    base: dict[str, Any],
    head: dict[str, Any],
    exceptions: set[tuple[str, str]],
) -> list[str]:
    failures: list[str] = []
    base_global = base.get("global") or {}
    head_global = head.get("global") or {}
    for metric in METRICS:
        if metric not in base_global or metric not in head_global:
            failures.append(f"global.{metric} is missing from a thresholds file")
            continue
        before = float(base_global[metric])
        after = float(head_global[metric])
        if after < before and ("global", metric) not in exceptions:
            failures.append(
                f"global.{metric} decreased from {before:g} to {after:g}; "
                "coverage floors must not go down"
            )
    base_globs = base.get("globs") or {}
    head_globs = head.get("globs") or {}
    for glob_name, metrics in base_globs.items():
        head_metrics = head_globs.get(glob_name)
        if not isinstance(metrics, dict) or not isinstance(head_metrics, dict):
            failures.append(f"glob {glob_name} is missing from head thresholds")
            continue
        for metric, before in metrics.items():
            if metric not in head_metrics:
                failures.append(f"{glob_name}.{metric} is missing from head thresholds")
                continue
            after = float(head_metrics[metric])
            if after < float(before) and (glob_name, metric) not in exceptions:
                failures.append(
                    f"{glob_name}.{metric} decreased from {before} to {after:g}; "
                    "coverage floors must not go down"
                )
    return failures


def check_lag(actual: float, floor: float, label: str) -> str | None:
    if actual >= floor + 5:
        return (
            f"{label} actual {actual:.2f}% exceeds floor {floor:g}% by 5pp or more. "
            f"Raise the floor to {suggested_floor(actual)} "
            f"(floor(actual) - 2) in frontend/coverage-thresholds.json."
        )
    return None


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--thresholds", type=Path, required=True)
    parser.add_argument("--summary", type=Path, required=True)
    parser.add_argument("--base-thresholds", type=Path)
    parser.add_argument("--exceptions", type=Path)
    args = parser.parse_args()

    thresholds = load_json(args.thresholds)
    summary = load_json(args.summary)
    failures: list[str] = []

    if args.base_thresholds is not None:
        if args.base_thresholds.is_file():
            failures.extend(
                compare_thresholds(
                    load_json(args.base_thresholds),
                    thresholds,
                    parse_exceptions(args.exceptions, date.today()),
                )
            )
        else:
            print(
                f"note: no base thresholds at {args.base_thresholds}; skip decrease check",
                file=sys.stderr,
            )

    global_floors = thresholds.get("global")
    if not isinstance(global_floors, dict):
        failures.append("thresholds.global must be an object")
    else:
        for metric in METRICS:
            if metric not in global_floors:
                failures.append(f"thresholds.global.{metric} is missing")
                continue
            lag = check_lag(metric_pct(summary, metric), float(global_floors[metric]), f"global.{metric}")
            if lag:
                failures.append(lag)

    for glob_name, metrics in (thresholds.get("globs") or {}).items():
        if not isinstance(metrics, dict):
            failures.append(f"thresholds.globs.{glob_name} must be an object")
            continue
        needle = glob_name.replace("/**", "/").replace("*", "")
        actual = glob_line_pct(summary, f"/{needle.strip('/')}/")
        if actual is None:
            failures.append(f"no coverage files matched glob {glob_name}")
            continue
        if "lines" in metrics:
            lag = check_lag(actual, float(metrics["lines"]), f"{glob_name}.lines")
            if lag:
                failures.append(lag)

    if failures:
        print("quality ratchet failed:", file=sys.stderr)
        for item in failures:
            print(f"  - {item}", file=sys.stderr)
        return 1

    print("quality ratchet passed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
