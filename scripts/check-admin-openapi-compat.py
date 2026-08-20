#!/usr/bin/env python3
"""Fail when the admin OpenAPI contract removes or renames operations without a major bump.

Compares --current against --base. If --base is omitted or the file is missing
(first commit of the contract), the check is skipped.

Exit codes: 0 pass or skip, 1 breaking change without a major version bump.
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path
from typing import Any


def load_document(path: Path) -> dict[str, Any]:
    data = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(data, dict):
        raise ValueError(f"{path} is not a JSON object")
    return data


def version_tuple(document: dict[str, Any]) -> tuple[int, int, int]:
    raw = document.get("info", {}).get("version", "0.0.0")
    if not isinstance(raw, str):
        raise ValueError("info.version must be a string")
    parts = raw.split(".")
    if len(parts) != 3 or not all(part.isdigit() for part in parts):
        raise ValueError(f"info.version must be MAJOR.MINOR.PATCH, got {raw!r}")
    return int(parts[0]), int(parts[1]), int(parts[2])


def operations(document: dict[str, Any]) -> dict[tuple[str, str], str | None]:
    paths = document.get("paths")
    if not isinstance(paths, dict):
        return {}
    found: dict[tuple[str, str], str | None] = {}
    for path, item in paths.items():
        if not isinstance(path, str) or not isinstance(item, dict):
            continue
        for method, operation in item.items():
            if method.startswith("x-") or method in {"parameters", "summary", "description", "servers"}:
                continue
            if not isinstance(operation, dict):
                continue
            operation_id = operation.get("operationId")
            found[(path, method.lower())] = operation_id if isinstance(operation_id, str) else None
    return found


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--current", required=True, type=Path)
    parser.add_argument("--base", type=Path, default=None)
    args = parser.parse_args()

    if args.base is None or not args.base.is_file():
        print("skip: no base admin OpenAPI contract to compare")
        return 0

    current = load_document(args.current)
    base = load_document(args.base)
    current_ops = operations(current)
    base_ops = operations(base)
    current_version = version_tuple(current)
    base_version = version_tuple(base)
    major_bumped = current_version[0] > base_version[0]

    errors: list[str] = []
    for key, operation_id in sorted(base_ops.items()):
        path, method = key
        if key not in current_ops:
            errors.append(f"removed {method.upper()} {path}")
            continue
        new_id = current_ops[key]
        if operation_id is not None and new_id != operation_id:
            errors.append(
                f"renamed {method.upper()} {path} operationId {operation_id!r} -> {new_id!r}"
            )

    if errors and not major_bumped:
        print(
            "error: breaking admin OpenAPI changes require a major contract version bump:",
            file=sys.stderr,
        )
        for error in errors:
            print(f"  {error}", file=sys.stderr)
        return 1

    if errors:
        print(
            f"ok: {len(errors)} breaking change(s) with major version bump "
            f"{base_version} -> {current_version}"
        )
        return 0

    print("ok: no removed or renamed admin operations")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exc:
        print(f"error: {exc}", file=sys.stderr)
        raise SystemExit(2) from exc
