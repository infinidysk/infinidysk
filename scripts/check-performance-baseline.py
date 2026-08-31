#!/usr/bin/env python3
"""Compare performance-report JSON against a committed baseline.

Deterministic fields must match exactly (and be identical across candidates).
Timing fields use floored envelopes; --deterministic-only skips them.

Exit codes: 0 pass (warnings allowed), 1 envelope failure, 2 deterministic/schema failure.
"""

from __future__ import annotations

import argparse
import json
import os
import sys
from datetime import datetime, timezone
from decimal import ROUND_HALF_UP, Decimal
from pathlib import Path
from typing import Any


SCHEMA_VERSION = 1
LATENCY_FLOOR_MS = 15.0
CPU_FLOOR_S = 0.25
ENVELOPE_MULTIPLIER = 3.0
REBASELINE_HINT = (
    "To re-baseline: Actions → Performance → Run workflow → rebaseline, "
    "or locally: python3 scripts/check-performance-baseline.py "
    "--candidates <report.json> "
    "--write-baseline backend.Benchmarks/Baselines/<report>-baseline.json"
)


def round3(value: float) -> float:
    quantized = Decimal(str(value)).quantize(Decimal("0.001"), rounding=ROUND_HALF_UP)
    return float(quantized)


def as_number(value: Any, *, label: str) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise ValueError(f"{label} is not a number: {value!r}")
    return float(value)


def as_int(value: Any, *, label: str) -> int:
    number = as_number(value, label=label)
    if number != int(number):
        raise ValueError(f"{label} is not an integer: {value!r}")
    return int(number)


def load_json(path: Path) -> dict[str, Any]:
    data = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(data, dict):
        raise ValueError(f"{path} is not a JSON object")
    return data


def scenario_names(report: dict[str, Any]) -> list[str]:
    scenarios = report.get("scenarios")
    if not isinstance(scenarios, dict):
        raise ValueError("missing scenarios object")
    return list(scenarios.keys())


def classify_timing_field(name: str) -> str:
    lowered = name.lower()
    if "throughput" in lowered:
        return "throughput"
    if "cpu" in lowered:
        return "cpu"
    return "latency"


def timing_envelope(median: float, kind: str) -> dict[str, Any]:
    if kind == "throughput":
        return {
            "baseline": round3(median),
            "floor": round3(median / ENVELOPE_MULTIPLIER),
            "policy": "fail",
        }
    floor = CPU_FLOOR_S if kind == "cpu" else LATENCY_FLOOR_MS
    return {
        "baseline": round3(median),
        "envelope": round3(max(ENVELOPE_MULTIPLIER * median, median + floor)),
        "policy": "warn" if kind == "cpu" else "fail",
    }


def median(values: list[float]) -> float:
    ordered = sorted(values)
    count = len(ordered)
    if count == 0:
        raise ValueError("median of empty list")
    mid = count // 2
    if count % 2:
        return ordered[mid]
    return (ordered[mid - 1] + ordered[mid]) / 2.0


def format_set(names: list[str]) -> str:
    return "[" + ", ".join(names) + "]"


class Gate:
    def __init__(self) -> None:
        self.schema_errors: list[str] = []
        self.deterministic_errors: list[str] = []
        self.envelope_failures: list[str] = []
        self.warnings: list[str] = []
        self.rows: list[tuple[str, str, str, str, str, str]] = []

    def error_schema(self, message: str) -> None:
        self.schema_errors.append(message)

    def error_deterministic(self, message: str) -> None:
        self.deterministic_errors.append(message)

    def fail_envelope(self, message: str) -> None:
        self.envelope_failures.append(message)

    def warn(self, message: str) -> None:
        self.warnings.append(message)

    def note(
        self,
        scenario: str,
        field: str,
        kind: str,
        result: str,
        expected: str,
        actual: str,
    ) -> None:
        self.rows.append((scenario, field, kind, result, expected, actual))

    def exit_code(self) -> int:
        if self.schema_errors or self.deterministic_errors:
            return 2
        if self.envelope_failures:
            return 1
        return 0


def validate_candidate_shape(
    gate: Gate, path: Path, report: dict[str, Any], *, require_timing: bool
) -> None:
    prefix = str(path)
    version = report.get("schemaVersion")
    if version != SCHEMA_VERSION:
        gate.error_schema(
            f"{prefix} schemaVersion expected {SCHEMA_VERSION} but was {version!r}"
        )
    if not isinstance(report.get("report"), str) or not report["report"]:
        gate.error_schema(f"{prefix} missing report name")
    scenarios = report.get("scenarios")
    if not isinstance(scenarios, dict) or not scenarios:
        gate.error_schema(f"{prefix} missing scenarios")
        return
    for name, scenario in scenarios.items():
        if not isinstance(scenario, dict):
            gate.error_schema(f"{prefix} scenario {name} is not an object")
            continue
        deterministic = scenario.get("deterministic")
        if not isinstance(deterministic, dict) or not deterministic:
            gate.error_schema(f"{prefix}/{name} missing deterministic object")
        timing = scenario.get("timing")
        if require_timing and (not isinstance(timing, dict) or not timing):
            gate.error_schema(f"{prefix}/{name} missing timing object")


def deterministic_map(scenario: dict[str, Any]) -> dict[str, int]:
    deterministic = scenario.get("deterministic")
    if not isinstance(deterministic, dict):
        raise ValueError("missing deterministic object")
    return {
        key: as_int(value, label=key) for key, value in deterministic.items()
    }


def candidate_timing_map(scenario: dict[str, Any]) -> dict[str, float]:
    timing = scenario.get("timing")
    if not isinstance(timing, dict):
        raise ValueError("missing timing object")
    return {key: as_number(value, label=key) for key, value in timing.items()}


def compare_deterministic(
    gate: Gate,
    report_name: str,
    baseline_scenarios: dict[str, Any],
    candidates: list[tuple[Path, dict[str, Any]]],
) -> None:
    names = list(baseline_scenarios.keys())
    for path, report in candidates:
        candidate_names = scenario_names(report)
        if set(candidate_names) != set(names):
            gate.error_schema(
                f"{report_name} scenarios expected {format_set(names)} "
                f"but {path} has {format_set(candidate_names)}"
            )

    for name in names:
        baseline_det = deterministic_map(baseline_scenarios[name])
        per_candidate: list[dict[str, int]] = []
        for path, report in candidates:
            scenarios = report.get("scenarios")
            if not isinstance(scenarios, dict) or name not in scenarios:
                continue
            try:
                per_candidate.append(deterministic_map(scenarios[name]))
            except ValueError as exc:
                gate.error_schema(f"{path}/{name} {exc}")
        if not per_candidate:
            continue

        keys = list(baseline_det.keys())
        for candidate_det in per_candidate:
            if set(candidate_det.keys()) != set(keys):
                gate.error_deterministic(
                    f"{report_name}/{name} deterministic fields expected "
                    f"{format_set(keys)} but was {format_set(list(candidate_det.keys()))}"
                )

        for key in keys:
            values = [item.get(key) for item in per_candidate]
            unique = {value for value in values if value is not None}
            if len(unique) > 1:
                gate.error_deterministic(
                    f"{report_name}/{name}/{key} candidates disagree: {values}. "
                    "Deterministic variance is harness nondeterminism."
                )
                gate.note(name, key, "deterministic", "fail", str(baseline_det[key]), str(values))
                continue
            actual = next(iter(unique)) if unique else None
            expected = baseline_det[key]
            if actual != expected:
                gate.error_deterministic(
                    f"{report_name}/{name}/{key} expected {expected} but was {actual}"
                )
                gate.note(name, key, "deterministic", "fail", str(expected), str(actual))
            else:
                gate.note(name, key, "deterministic", "pass", str(expected), str(actual))


def compare_timing(
    gate: Gate,
    report_name: str,
    baseline_scenarios: dict[str, Any],
    candidates: list[tuple[Path, dict[str, Any]]],
) -> None:
    for name, baseline_scenario in baseline_scenarios.items():
        timing_spec = baseline_scenario.get("timing")
        if not isinstance(timing_spec, dict):
            gate.error_schema(f"{report_name}/{name} baseline missing timing object")
            continue
        collected: dict[str, list[float]] = {field: [] for field in timing_spec}
        for path, report in candidates:
            scenarios = report.get("scenarios")
            if not isinstance(scenarios, dict) or name not in scenarios:
                continue
            try:
                values = candidate_timing_map(scenarios[name])
            except ValueError as exc:
                gate.error_schema(f"{path}/{name} {exc}")
                continue
            for field in timing_spec:
                if field not in values:
                    gate.error_schema(f"{path}/{name} missing timing field {field}")
                    continue
                collected[field].append(values[field])

        for field, spec in timing_spec.items():
            if not isinstance(spec, dict):
                gate.error_schema(f"{report_name}/{name}/{field} baseline timing is not an object")
                continue
            samples = collected.get(field) or []
            if not samples:
                continue
            observed = median(samples)
            policy = str(spec.get("policy") or "fail")
            kind = classify_timing_field(field)
            evaluate_timing_field(
                gate,
                report_name=report_name,
                scenario=name,
                field=field,
                spec=spec,
                observed=observed,
                policy=policy,
                kind=kind,
            )


def evaluate_timing_field(
    gate: Gate,
    *,
    report_name: str,
    scenario: str,
    field: str,
    spec: dict[str, Any],
    observed: float,
    policy: str,
    kind: str,
) -> None:
    baseline = as_number(spec["baseline"], label=f"{scenario}/{field} baseline")
    label = f"{report_name}/{scenario}/{field}"
    if kind == "throughput":
        floor = as_number(spec["floor"], label=f"{scenario}/{field} floor")
        midpoint = baseline - 0.5 * (baseline - floor)
        expected = f"floor {floor:.3f} (baseline {baseline:.3f})"
        actual = f"median {observed:.3f}"
        if observed < floor:
            message = (
                f"{label} median {observed:.3f} is below floor {floor:.3f} "
                f"(baseline {baseline:.3f})"
            )
            if policy == "fail":
                gate.fail_envelope(message)
                gate.note(scenario, field, "timing", "fail", expected, actual)
            else:
                gate.warn(message)
                gate.note(scenario, field, "timing", "warn", expected, actual)
        elif observed < midpoint:
            gate.warn(
                f"{label} median {observed:.3f} is below warn midpoint {midpoint:.3f} "
                f"(baseline {baseline:.3f}, floor {floor:.3f})"
            )
            gate.note(scenario, field, "timing", "warn", expected, actual)
        else:
            gate.note(scenario, field, "timing", "pass", expected, actual)
        return

    envelope = as_number(spec["envelope"], label=f"{scenario}/{field} envelope")
    midpoint = baseline + 0.5 * (envelope - baseline)
    expected = f"envelope {envelope:.3f} (baseline {baseline:.3f})"
    actual = f"median {observed:.3f}"
    if observed > envelope:
        message = (
            f"{label} median {observed:.3f} exceeds envelope {envelope:.3f} "
            f"(baseline {baseline:.3f})"
        )
        if policy == "fail":
            gate.fail_envelope(message)
            gate.note(scenario, field, "timing", "fail", expected, actual)
        else:
            gate.warn(message)
            gate.note(scenario, field, "timing", "warn", expected, actual)
    elif observed > midpoint:
        gate.warn(
            f"{label} median {observed:.3f} exceeds warn midpoint {midpoint:.3f} "
            f"(baseline {baseline:.3f}, envelope {envelope:.3f})"
        )
        gate.note(scenario, field, "timing", "warn", expected, actual)
    else:
        gate.note(scenario, field, "timing", "pass", expected, actual)


def write_baseline(
    path: Path,
    candidates: list[tuple[Path, dict[str, Any]]],
) -> None:
    first = candidates[0][1]
    names = scenario_names(first)
    scenarios: dict[str, Any] = {}
    for name in names:
        det = deterministic_map(first["scenarios"][name])
        timing_keys = list(candidate_timing_map(first["scenarios"][name]).keys())
        timing: dict[str, Any] = {}
        for field in timing_keys:
            samples = [
                candidate_timing_map(report["scenarios"][name])[field]
                for _, report in candidates
            ]
            timing[field] = timing_envelope(median(samples), classify_timing_field(field))
        scenarios[name] = {"deterministic": det, "timing": timing}

    meta = first.get("meta") if isinstance(first.get("meta"), dict) else {}
    document = {
        "schemaVersion": SCHEMA_VERSION,
        "report": first.get("report"),
        "meta": {
            "commit": meta.get("commit", "unknown"),
            "generatedAt": datetime.now(timezone.utc).isoformat(),
            "runner": os.environ.get("RUNNER_OS", "local"),
        },
        "scenarios": scenarios,
    }
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(document, indent=2) + "\n", encoding="utf-8")
    print(f"Wrote baseline {path}")


def write_summary(path: Path | None, gate: Gate, report_name: str) -> None:
    if path is None:
        return
    lines = [
        f"## Performance gate ({report_name})",
        "",
        f"- Deterministic/schema errors: **{len(gate.schema_errors) + len(gate.deterministic_errors)}**",
        f"- Envelope failures: **{len(gate.envelope_failures)}**",
        f"- Warnings: **{len(gate.warnings)}**",
        "",
        "| Scenario | Field | Kind | Result | Expected | Actual |",
        "| --- | --- | --- | --- | --- | --- |",
    ]
    if gate.rows:
        for scenario, field, kind, result, expected, actual in gate.rows:
            lines.append(
                f"| `{scenario}` | `{field}` | {kind} | {result} | {expected} | {actual} |"
            )
    else:
        lines.append("| _none_ |  |  |  |  |  |")
    lines.append("")
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("a", encoding="utf-8") as handle:
        handle.write("\n".join(lines))
        if not lines[-1].endswith("\n"):
            handle.write("\n")


def emit(gate: Gate) -> int:
    for message in gate.warnings:
        print(f"warning: {message}")
    for message in gate.schema_errors + gate.deterministic_errors + gate.envelope_failures:
        print(f"error: {message}", file=sys.stderr)
    code = gate.exit_code()
    if code == 2:
        print(
            f"Performance gate failed: deterministic/schema error(s). {REBASELINE_HINT}",
            file=sys.stderr,
        )
    elif code == 1:
        print(
            f"Performance gate failed: envelope failure(s). {REBASELINE_HINT}",
            file=sys.stderr,
        )
    elif gate.warnings:
        print(f"Performance gate passed with {len(gate.warnings)} warning(s).")
    else:
        print("Performance gate passed.")
    return code


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline", type=Path, default=None, help="Committed baseline JSON")
    parser.add_argument(
        "--candidates",
        nargs="+",
        type=Path,
        required=True,
        help="Candidate report JSON files",
    )
    parser.add_argument(
        "--deterministic-only",
        action="store_true",
        help="Skip timing/envelope comparison",
    )
    parser.add_argument(
        "--summary",
        type=Path,
        default=None,
        help="Append a Markdown summary (typically $GITHUB_STEP_SUMMARY)",
    )
    parser.add_argument(
        "--write-baseline",
        type=Path,
        default=None,
        help="Write a baseline from candidate medians and envelope formulas",
    )
    args = parser.parse_args(argv)

    if args.baseline is None and args.write_baseline is None:
        parser.error("either --baseline or --write-baseline is required")

    candidates: list[tuple[Path, dict[str, Any]]] = []
    gate = Gate()
    for path in args.candidates:
        try:
            candidates.append((path, load_json(path)))
        except (OSError, ValueError, json.JSONDecodeError) as exc:
            gate.error_schema(f"{path} could not be read: {exc}")
    if not candidates:
        write_summary(args.summary, gate, "unknown")
        return emit(gate)

    require_timing = args.write_baseline is not None or not args.deterministic_only
    for path, report in candidates:
        try:
            validate_candidate_shape(gate, path, report, require_timing=require_timing)
        except ValueError as exc:
            gate.error_schema(f"{path} {exc}")

    first_report = candidates[0][1].get("report")
    for path, report in candidates[1:]:
        if report.get("report") != first_report:
            gate.error_schema(
                f"{path} report expected {first_report!r} but was {report.get('report')!r}"
            )
        try:
            if set(scenario_names(report)) != set(scenario_names(candidates[0][1])):
                gate.error_schema(
                    f"{path} scenarios expected {format_set(scenario_names(candidates[0][1]))} "
                    f"but has {format_set(scenario_names(report))}"
                )
        except ValueError as exc:
            gate.error_schema(f"{path} {exc}")

    # Cross-candidate deterministic identity even without a baseline.
    if not gate.schema_errors:
        try:
            names = scenario_names(candidates[0][1])
            for name in names:
                maps = []
                for path, report in candidates:
                    maps.append(deterministic_map(report["scenarios"][name]))
                for key in maps[0]:
                    values = [item[key] for item in maps]
                    if len(set(values)) > 1:
                        gate.error_deterministic(
                            f"{first_report}/{name}/{key} candidates disagree: {values}. "
                            "Deterministic variance is harness nondeterminism."
                        )
        except (ValueError, KeyError) as exc:
            gate.error_schema(str(exc))

    report_name = str(first_report or "unknown")
    if args.baseline is not None:
        try:
            baseline = load_json(args.baseline)
        except (OSError, ValueError, json.JSONDecodeError) as exc:
            gate.error_schema(f"{args.baseline} could not be read: {exc}")
            write_summary(args.summary, gate, report_name)
            return emit(gate)
        if baseline.get("schemaVersion") != SCHEMA_VERSION:
            gate.error_schema(
                f"{args.baseline} schemaVersion expected {SCHEMA_VERSION} "
                f"but was {baseline.get('schemaVersion')!r}"
            )
        if baseline.get("report") != first_report:
            gate.error_schema(
                f"report expected {baseline.get('report')!r} but candidates are {first_report!r}"
            )
        baseline_scenarios = baseline.get("scenarios")
        if not isinstance(baseline_scenarios, dict) or not baseline_scenarios:
            gate.error_schema(f"{args.baseline} missing scenarios")
        else:
            compare_deterministic(gate, report_name, baseline_scenarios, candidates)
            if not args.deterministic_only:
                compare_timing(gate, report_name, baseline_scenarios, candidates)

    if args.write_baseline is not None and gate.exit_code() != 2:
        try:
            write_baseline(args.write_baseline, candidates)
        except (OSError, ValueError, KeyError) as exc:
            gate.error_schema(f"--write-baseline failed: {exc}")

    write_summary(args.summary, gate, report_name)
    return emit(gate)


if __name__ == "__main__":
    raise SystemExit(main())
