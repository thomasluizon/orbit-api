#!/usr/bin/env python3
"""Flag BenchmarkDotNet means that regressed past a tolerance over a committed baseline.

Report-only by design: prints a per-benchmark PASS / SEED / REGRESSION line, emits GitHub
``::warning::`` annotations for regressions and for benchmarks missing from a run, but always
exits 0 so a noisy nightly benchmark never reds the pipeline. Mirrors ``check_coverage.py``'s
confine-then-compare shape. A baseline mean of 0 (or absent) means "not yet seeded" — the first
real nightly reports the measured numbers, which are then committed into ``bench/baseline.json``.

    check_bench.py <results-dir> <baseline-json> [tolerance-ratio]
"""
import glob
import json
import os
import sys


def _confine(candidate: str, base: str) -> str:
    """Resolve candidate and confirm it stays within base, rejecting path traversal."""
    resolved = os.path.realpath(candidate)
    if resolved != base and not resolved.startswith(base + os.sep):
        raise ValueError(f"Refusing path outside {base}: {candidate}")
    return resolved


def _load_measured(results_dir: str, base: str) -> dict[str, float]:
    measured: dict[str, float] = {}
    for report in glob.glob(os.path.join(results_dir, "*-report-full.json")):
        with open(_confine(report, base), encoding="utf-8") as handle:
            document = json.load(handle)
        for benchmark in document.get("Benchmarks", []):
            statistics = benchmark.get("Statistics") or {}
            mean = statistics.get("Mean")
            if mean is None:
                continue
            measured[f"{benchmark.get('Type')}.{benchmark.get('Method')}"] = float(mean)
    return measured


def main() -> int:
    results_dir, baseline_path = sys.argv[1], sys.argv[2]
    base = os.path.realpath(os.getcwd())
    safe_results_dir = _confine(results_dir, base)

    with open(_confine(baseline_path, base), encoding="utf-8") as handle:
        baseline = json.load(handle)
    tolerance = float(sys.argv[3]) if len(sys.argv) > 3 else float(baseline.get("toleranceRatio", 0.5))
    baseline_means: dict[str, float] = baseline.get("means", {})

    measured = _load_measured(safe_results_dir, base)
    if not measured:
        print(f"No *-report-full.json found under {results_dir}", file=sys.stderr)
        return 0

    regressions = 0
    for key in sorted(measured):
        mean = measured[key]
        recorded = baseline_means.get(key)
        if recorded is None or recorded <= 0:
            print(f"SEED {key}: {mean:.1f} ns (no baseline yet)")
            continue
        threshold = recorded * (1 + tolerance)
        if mean > threshold:
            regressions += 1
            print(f"::warning::REGRESSION {key}: {mean:.1f} ns > {threshold:.1f} ns "
                  f"(baseline {recorded:.1f} ns +{int(tolerance * 100)}%)")
        else:
            print(f"PASS {key}: {mean:.1f} ns (baseline {recorded:.1f} ns, limit {threshold:.1f} ns)")

    for key in sorted(set(baseline_means) - set(measured)):
        print(f"::warning::MISSING {key}: present in baseline but not measured this run")

    print(f"\n{len(measured)} benchmark(s) checked; {regressions} regression(s) flagged (report-only, exit 0).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
