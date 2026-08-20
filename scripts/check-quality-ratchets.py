#!/usr/bin/env python3
"""Fail when quality floors drop, lag actual coverage, or ESLint ceilings rise.

Coverage thresholds live in frontend/coverage-thresholds.json. Temporary
decreases require a complete, unexpired entry in quality/ratchet-exceptions.json.
The ESLint --max-warnings ceiling has no exception path and must not increase.
"""

from __future__ import annotations

import argparse
import json
import math
import re
import sys
from datetime import date, datetime, timezone
from pathlib import Path
from typing import Any


METRICS = ("branches", "functions", "lines", "statements")
REQUIRED_EXCEPTION_FIELDS = (
    "scope",
    "metric",
    "oldValue",
    "newValue",
    "issueUrl",
    "reason",
    "expiresOn",
)
ISSUE_URL_RE = re.compile(
    r"^https://github.com/infinidysk/infinidysk/(issues|pull)/\d+$"
)
MAX_WARNINGS_RE = re.compile(r"--max-warnings(?:\s+|=)(\d+)")


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
        raise SystemExit(
            f"error: coverage summary total.{metric}.pct is not a number"
        ) from exc


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


def parse_exceptions(path: Path, today: date) -> tuple[set[tuple[str, str, float, float]], list[str]]:
    data = load_json(path)
    raw_list = data.get("exceptions")
    if not isinstance(raw_list, list):
        return set(), [f"{path}: exceptions must be an array"]

    allowed: set[tuple[str, str, float, float]] = set()
    failures: list[str] = []
    for index, raw in enumerate(raw_list):
        label = f"{path} exceptions[{index}]"
        if not isinstance(raw, dict):
            failures.append(f"{label} must be an object")
            continue
        missing = [field for field in REQUIRED_EXCEPTION_FIELDS if field not in raw]
        if missing:
            failures.append(f"{label} is missing {', '.join(missing)}")
            continue
        scope = str(raw["scope"]).strip()
        metric = str(raw["metric"]).strip()
        reason = str(raw["reason"]).strip()
        issue_url = str(raw["issueUrl"]).strip()
        if not scope or not metric:
            failures.append(f"{label} scope and metric must be non-empty")
        if not reason:
            failures.append(f"{label} reason must be non-empty")
        if not ISSUE_URL_RE.match(issue_url):
            failures.append(
                f"{label} issueUrl must be an infinidysk issue or pull URL"
            )
        try:
            old_value = float(raw["oldValue"])
            new_value = float(raw["newValue"])
        except (TypeError, ValueError):
            failures.append(f"{label} oldValue and newValue must be numbers")
            continue
        if new_value >= old_value:
            failures.append(f"{label} newValue must be lower than oldValue")
        try:
            expires_on = date.fromisoformat(str(raw["expiresOn"]))
        except ValueError:
            failures.append(f"{label} expiresOn must be YYYY-MM-DD")
            continue
        if expires_on < today:
            failures.append(f"{label} expired on {expires_on.isoformat()}")
            continue
        allowed.add((scope, metric, old_value, new_value))
    return allowed, failures


def is_excepted(
    exceptions: set[tuple[str, str, float, float]],
    scope: str,
    metric: str,
    before: float,
    after: float,
) -> bool:
    return (scope, metric, before, after) in exceptions


def compare_thresholds(
    base: dict[str, Any],
    head: dict[str, Any],
    exceptions: set[tuple[str, str, float, float]],
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
        if after < before and not is_excepted(exceptions, "global", metric, before, after):
            failures.append(
                f"global.{metric} decreased from {before:g} to {after:g}; "
                "add a reviewed quality/ratchet-exceptions.json entry or restore the floor"
            )
    base_globs = base.get("globs") or {}
    head_globs = head.get("globs") or {}
    for glob_name, metrics in base_globs.items():
        head_metrics = head_globs.get(glob_name)
        if not isinstance(metrics, dict) or not isinstance(head_metrics, dict):
            failures.append(f"glob {glob_name} is missing from head thresholds")
            continue
        for metric, before_raw in metrics.items():
            if metric not in head_metrics:
                failures.append(f"{glob_name}.{metric} is missing from head thresholds")
                continue
            before = float(before_raw)
            after = float(head_metrics[metric])
            if after < before and not is_excepted(
                exceptions, glob_name, metric, before, after
            ):
                failures.append(
                    f"{glob_name}.{metric} decreased from {before:g} to {after:g}; "
                    "add a reviewed quality/ratchet-exceptions.json entry or restore the floor"
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


def eslint_ceiling(package: dict[str, Any], path: Path) -> int:
    lint = (package.get("scripts") or {}).get("lint")
    if not isinstance(lint, str) or not lint.strip():
        raise SystemExit(f"error: {path} is missing scripts.lint")
    matches = MAX_WARNINGS_RE.findall(lint)
    if not matches:
        raise SystemExit(
            f"error: {path} scripts.lint must pass --max-warnings N; "
            "an omitted ceiling is an unlimited-warning path"
        )
    return int(matches[-1])


def compare_eslint_ceiling(base: Path, head: Path) -> list[str]:
    before = eslint_ceiling(load_json(base), base)
    after = eslint_ceiling(load_json(head), head)
    if after > before:
        return [
            f"ESLint --max-warnings rose from {before} to {after}; "
            "the warning ceiling can only decrease"
        ]
    return []


def lag_excepted(
    exceptions: set[tuple[str, str, float, float]],
    scope: str,
    metric: str,
    floor: float,
) -> bool:
    return any(
        item[0] == scope and item[1] == metric and item[3] == floor
        for item in exceptions
    )


def check_thresholds(
    thresholds: dict[str, Any],
    summary: dict[str, Any] | None,
    exceptions: set[tuple[str, str, float, float]],
) -> list[str]:
    failures: list[str] = []
    global_floors = thresholds.get("global")
    if not isinstance(global_floors, dict):
        failures.append("thresholds.global must be an object")
        return failures
    for metric in METRICS:
        if metric not in global_floors:
            failures.append(f"thresholds.global.{metric} is missing")
            continue
        floor = float(global_floors[metric])
        if summary is not None and not lag_excepted(exceptions, "global", metric, floor):
            lag = check_lag(
                metric_pct(summary, metric),
                floor,
                f"global.{metric}",
            )
            if lag:
                failures.append(lag)

    globs = thresholds.get("globs")
    if globs is None:
        return failures
    if not isinstance(globs, dict):
        failures.append("thresholds.globs must be an object")
        return failures
    for glob_name, metrics in globs.items():
        if not isinstance(metrics, dict):
            failures.append(f"thresholds.globs.{glob_name} must be an object")
            continue
        if summary is None or "lines" not in metrics:
            continue
        floor = float(metrics["lines"])
        if lag_excepted(exceptions, glob_name, "lines", floor):
            continue
        needle = glob_name.replace("/**", "/").replace("*", "")
        actual = glob_line_pct(summary, f"/{needle.strip('/')}/")
        if actual is None:
            failures.append(f"no coverage files matched glob {glob_name}")
            continue
        lag = check_lag(actual, floor, f"{glob_name}.lines")
        if lag:
            failures.append(lag)
    return failures


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--thresholds", type=Path)
    parser.add_argument("--summary", type=Path)
    parser.add_argument("--base-thresholds", type=Path)
    parser.add_argument("--package-json", type=Path)
    parser.add_argument("--base-package-json", type=Path)
    parser.add_argument("--exceptions", type=Path)
    parser.add_argument(
        "--today",
        type=str,
        default=None,
        help="Override today (YYYY-MM-DD) for exception expiry checks",
    )
    args = parser.parse_args(argv)

    today = (
        date.fromisoformat(args.today)
        if args.today
        else datetime.now(timezone.utc).date()
    )
    failures: list[str] = []
    exceptions: set[tuple[str, str, float, float]] = set()

    if args.exceptions is not None:
        exceptions, exception_failures = parse_exceptions(args.exceptions, today)
        failures.extend(exception_failures)

    if args.package_json is not None:
        eslint_ceiling(load_json(args.package_json), args.package_json)
        if args.base_package_json is not None:
            if args.base_package_json.is_file():
                failures.extend(
                    compare_eslint_ceiling(args.base_package_json, args.package_json)
                )
            else:
                print(
                    f"note: no base package.json at {args.base_package_json}; "
                    "skip ESLint ceiling decrease check",
                    file=sys.stderr,
                )

    if args.thresholds is not None:
        thresholds = load_json(args.thresholds)
        if args.base_thresholds is not None:
            if args.base_thresholds.is_file():
                failures.extend(
                    compare_thresholds(
                        load_json(args.base_thresholds),
                        thresholds,
                        exceptions,
                    )
                )
            else:
                print(
                    f"note: no base thresholds at {args.base_thresholds}; "
                    "skip decrease check",
                    file=sys.stderr,
                )
        summary = load_json(args.summary) if args.summary is not None else None
        failures.extend(check_thresholds(thresholds, summary, exceptions))

    if failures:
        print("quality ratchet failed:", file=sys.stderr)
        for item in failures:
            print(f"  - {item}", file=sys.stderr)
        return 1

    print("quality ratchet passed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
