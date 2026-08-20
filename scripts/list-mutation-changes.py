#!/usr/bin/env python3
"""Print Stryker --mutate globs for pilot files changed between two commits."""

from __future__ import annotations

import argparse
import json
import subprocess
from pathlib import Path


def load_files(baseline: Path) -> list[str]:
    data = json.loads(baseline.read_text(encoding="utf-8"))
    files: list[str] = []
    for scope in data.get("scopes") or []:
        if not str(scope.get("id") or "").startswith("backend."):
            continue
        files.extend(str(path) for path in scope.get("files") or [])
    return files


def git_changed(base: str, head: str, files: list[str]) -> list[str]:
    if not files:
        return []
    result = subprocess.run(
        ["git", "diff", "--name-only", base, head, "--", *files],
        check=True,
        capture_output=True,
        text=True,
    )
    return [line.strip() for line in result.stdout.splitlines() if line.strip()]


def to_mutate(path: str) -> str:
    relative = path.removeprefix("backend/")
    return "**/" + relative


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline", type=Path, required=True)
    parser.add_argument("--base")
    parser.add_argument("--head")
    parser.add_argument("--scope", default="changed")
    args = parser.parse_args()

    all_files = load_files(args.baseline)
    scope = args.scope
    if scope in ("all", "backend"):
        selected = all_files
    elif scope == "changed":
        if not args.base or not args.head:
            raise SystemExit("error: --base and --head are required for changed scope")
        selected = git_changed(args.base, args.head, all_files)
    else:
        data = json.loads(args.baseline.read_text(encoding="utf-8"))
        selected = []
        for item in data.get("scopes") or []:
            if item.get("id") == f"backend.{scope}" or item.get("id") == scope:
                selected.extend(item.get("files") or [])
        if not selected:
            raise SystemExit(f"error: unknown backend scope {scope}")

    if not selected:
        return 0
    for path in selected:
        print(to_mutate(path))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
