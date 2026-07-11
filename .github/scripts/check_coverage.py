#!/usr/bin/env python3
"""Fail CI when merged line coverage across Cobertura reports is below a floor.

The three test projects each instrument the same Orbit.* assemblies, so their
Cobertura reports overlap. Coverage is unioned per (source file, line): a line
counted as hit in any report is covered. Usage:

    check_coverage.py <results-dir> <min-line-percent>
"""
import glob
import os
import sys
import xml.etree.ElementTree as ET


def _confine(candidate: str, base: str) -> str:
    """Resolve candidate and confirm it stays within base, rejecting path traversal."""
    resolved = os.path.realpath(candidate)
    if resolved != base and not resolved.startswith(base + os.sep):
        raise ValueError(f"Refusing path outside {base}: {candidate}")
    return resolved


def main() -> int:
    results_dir, floor = sys.argv[1], float(sys.argv[2])
    base = os.path.realpath(os.getcwd())
    try:
        safe_results_dir = _confine(results_dir, base)
    except ValueError as error:
        print(error, file=sys.stderr)
        return 1

    reports = glob.glob(os.path.join(safe_results_dir, "**", "coverage.cobertura.xml"), recursive=True)
    if not reports:
        print(f"No coverage.cobertura.xml found under {results_dir}", file=sys.stderr)
        return 1

    hits: dict[tuple[str, str], int] = {}
    for report in reports:
        root = ET.parse(_confine(report, base)).getroot()
        for class_node in root.iter("class"):
            filename = class_node.get("filename", "")
            for line in class_node.iter("line"):
                key = (filename, line.get("number", ""))
                hits[key] = max(hits.get(key, 0), int(line.get("hits", "0")))

    total = len(hits)
    covered = sum(1 for count in hits.values() if count > 0)
    percent = covered / total * 100 if total else 0.0

    print(f"Merged line coverage: {percent:.2f}% ({covered}/{total}) across {len(reports)} report(s)")
    if percent < floor:
        print(f"Coverage {percent:.2f}% is below the {floor:.2f}% threshold", file=sys.stderr)
        return 1
    print(f"Coverage {percent:.2f}% meets the {floor:.2f}% threshold")
    return 0


if __name__ == "__main__":
    sys.exit(main())
