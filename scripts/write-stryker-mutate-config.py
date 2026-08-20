#!/usr/bin/env python3
"""Write a Stryker.NET config whose mutate list is exactly the given globs.

Passing -m on the CLI can merge with the default **/* glob and mutate the whole
backend. Always replace the config mutate list instead.
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--globs-file", type=Path)
    parser.add_argument("--test-case-filter")
    args = parser.parse_args()

    data = json.loads(args.base.read_text(encoding="utf-8"))
    config = data.get("stryker-config")
    if not isinstance(config, dict):
        raise SystemExit("error: missing stryker-config object")

    if args.globs_file is not None:
        globs = [
            line.strip()
            for line in args.globs_file.read_text(encoding="utf-8").splitlines()
            if line.strip()
        ]
    else:
        globs = [line.strip() for line in sys.stdin if line.strip()]

    if not globs:
        raise SystemExit("error: mutate list is empty")

    config["mutate"] = globs
    if args.test_case_filter:
        config["test-case-filter"] = args.test_case_filter
    else:
        config.pop("test-case-filter", None)

    args.output.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
