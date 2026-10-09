#!/usr/bin/env python3
"""Drive NonBlocking.Bench across implementations, builds and thread counts.

Each (scenario, implementation, build) runs in its own process, once per trial, and the
order of those processes is shuffled within every trial. Interleaving spreads slow drift on
a shared CI runner (noisy neighbours, thermal throttling) across all series instead of
letting it land on whichever series happened to run last.

Two kinds of comparison come out of one run:
  * NonBlocking vs System.Collections.Concurrent, for every --variant build;
  * candidate vs baseline when two --variant builds are given (first = candidate).
BCL runs once per trial, from the candidate build, since both builds share the runtime.

Ratios are reported as the ratio of medians. A row is called faster or slower only when an
exact two-sided permutation test on log rates rejects "no difference" at 0.05 after Holm
correction across the thread counts of that scenario. Permutation tests make no normality
assumption, which matters at these sample sizes; with 3 trials no row can reach significance,
which is the honest outcome. Seven trials (the default) allow it.

A run can be split into shards (--shard I/N) so that several runners measure in parallel.
Scenarios are assigned in fixed groups (GROUPS) of tables that are read against each other,
such as a key distribution and its adversarial variant, so each group lands on one machine.
Every comparison in a table stays within one scenario, so splitting changes no statistic;
tables from different groups may come from different machines, which the summary names.
--merge combines the shards' result files into one summary.

Standard library only, so it runs on a bare GitHub-hosted runner.
"""

from __future__ import annotations

import argparse
import json
import os
import random
import itertools
import math
import statistics
import subprocess
import sys
import re
import time
from collections import defaultdict
from pathlib import Path

SCENARIOS = ["get-str", "get-str-samecmp", "get-int-uniform", "get-int-topbits", "get-long-uniform",
             "get-long-topbits", "miss-int-uniform", "miss-int-topbits", "miss-long-topbits",
             "small-int-uniform", "small-long-uniform", "insert-long-mirrored", "grow-int", "grow-str",
             "mixed-str", "churn-str", "churn-long", "churn-burst", "readd-str", "enum-write"]
UNITS = {"enum-write": "M items/s", "insert-long-mirrored": "M keys inserted/s",
         "grow-int": "M keys inserted/s", "grow-str": "M keys inserted/s"}
# Scenarios read against each other share a group, and a group always runs on one shard:
# shards land on different CPU models, and a cross-table comparison is only meaningful on
# one machine. Every scenario belongs to exactly one group.
GROUPS = [
    ["get-str", "get-str-samecmp"],
    ["get-int-uniform", "get-int-topbits", "miss-int-uniform", "miss-int-topbits"],
    ["get-long-uniform", "get-long-topbits", "miss-long-topbits"],
    ["small-int-uniform", "small-long-uniform"],
    ["insert-long-mirrored", "grow-int", "grow-str"],
    ["mixed-str", "readd-str", "enum-write"],
    ["churn-str", "churn-long", "churn-burst"],
]
assert sorted(sc for g in GROUPS for sc in g) == sorted(SCENARIOS), "GROUPS must cover every scenario once"
# Lookup-only scenarios. In 'auto' mode they skip the oversubscribed count: with no writes and
# no locks, threads beyond the core count only time-slice the same lookups.
READ_ONLY = ("get-", "miss-", "small-")
# Settings that must agree across shards before their results can share one summary.
SHARED_SETTINGS = ("variants", "trials", "warmup_ms", "rewarm_ms", "duration_ms", "size", "live", "seed")
PERMUTATION_LIMIT = 20000
ALPHA = 0.05


def parse_args() -> argparse.Namespace:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--variant", action="append", metavar="LABEL=PATH",
                   help="build to run, e.g. head=out/head/NonBlocking.Bench.dll; repeat for A/B (first is the candidate)")
    p.add_argument("--scenarios", default="all", help="comma-separated names, or 'all'")
    p.add_argument("--shard", default="0/1", metavar="I/N",
                   help="run only the scenario groups assigned to shard I of N (0-based)")
    p.add_argument("--threads", default="auto",
                   help="comma-separated counts, or 'auto' (powers of two up to the core count, the core count, "
                        "and 2x for oversubscription except in lookup-only scenarios)")
    p.add_argument("--trials", type=int, default=7)
    p.add_argument("--warmup-ms", type=int, default=500)
    p.add_argument("--rewarm-ms", type=int, default=200,
                   help="warm-up for each thread count after the first in a process, once the code is compiled")
    p.add_argument("--duration-ms", type=int, default=1000)
    p.add_argument("--size", type=int, default=1_000_000)
    p.add_argument("--live", type=int, default=1_000)
    p.add_argument("--seed", type=int, default=42)
    p.add_argument("--dotnet", default="dotnet")
    p.add_argument("--out", type=Path, default=Path("bench-results.jsonl"))
    p.add_argument("--summary", type=Path, default=Path("bench-summary.md"))
    p.add_argument("--annotate", metavar="LABEL",
                   help="print GitHub Actions ::notice/::warning lines for candidate-vs-baseline results "
                        "instead of the summary; LABEL names the runner in each title")
    p.add_argument("--merge", nargs="+", type=Path, metavar="JSONL",
                   help="summarise these result files (one per shard) instead of running anything")
    args = p.parse_args()

    if args.merge:
        missing = [str(f) for f in args.merge if not f.is_file()]
        if missing:
            p.error(f"--merge: no such file: {', '.join(missing)}")
        return args

    if not args.variant:
        p.error("at least one --variant is required unless --merge is given")
    variants: list[tuple[str, Path]] = []
    for v in args.variant:
        label, sep, path = v.partition("=")
        if not sep or not label or not path:
            p.error(f"--variant expects LABEL=PATH, got {v!r}")
        if not Path(path).is_file():
            p.error(f"--variant {label}: {path} does not exist")
        variants.append((label, Path(path)))
    if len({label for label, _ in variants}) != len(variants):
        p.error("variant labels must be unique")
    if len(variants) > 2:
        p.error("at most two variants (candidate and baseline)")
    args.variants = variants

    m = re.fullmatch(r"(\d+)/(\d+)", args.shard)
    if not m or int(m[2]) < 1 or int(m[1]) >= int(m[2]):
        p.error(f"--shard expects I/N with 0 <= I < N, got {args.shard!r}")
    shard, shards = int(m[1]), int(m[2])

    requested = SCENARIOS if args.scenarios == "all" else args.scenarios.split(",")
    unknown = set(requested) - set(SCENARIOS)
    if unknown:
        p.error(f"unknown scenarios: {', '.join(sorted(unknown))}")
    args.scenario_list = shard_scenarios(requested, shard, shards)
    if args.trials < 2:
        p.error("--trials must be at least 2 to estimate spread")
    for name in ("warmup_ms", "rewarm_ms", "duration_ms"):
        if getattr(args, name) < 1:
            p.error(f"--{name.replace('_', '-')} must be positive")
    if args.threads == "auto":
        cores = available_cores()
        counts = auto_threads(cores)
        args.threads_by_scenario = {
            sc: [t for t in counts if t <= cores] if sc.startswith(READ_ONLY) else counts
            for sc in args.scenario_list}
    else:
        try:
            counts = [int(t) for t in args.threads.split(",")]
        except ValueError:
            p.error(f"--threads expects 'auto' or comma-separated integers, got {args.threads!r}")
        if any(t < 1 for t in counts):
            p.error("--threads values must be positive")
        args.threads_by_scenario = {sc: counts for sc in args.scenario_list}
    return args


def shard_scenarios(requested: list[str], shard: int, shards: int) -> list[str]:
    """The requested scenarios of the groups assigned to this shard.

    Groups go to shards largest first, each to the shard with the least estimated work so far
    (ties to the lowest index), which is deterministic, so every shard computes the same
    assignment. A write scenario's process runs one more thread count, hence the higher weight.
    """
    wanted = set(requested)
    groups = [[sc for sc in g if sc in wanted] for g in GROUPS]
    groups = [g for g in groups if g]
    cost = lambda g: sum(1.0 if sc.startswith(READ_ONLY) else 1.25 for sc in g)
    load = [0.0] * shards
    mine: set[str] = set()
    for g in sorted(groups, key=lambda g: (-cost(g), SCENARIOS.index(g[0]))):
        target = min(range(shards), key=lambda i: (load[i], i))
        load[target] += cost(g)
        if target == shard:
            mine.update(g)
    return [sc for sc in requested if sc in mine]


def available_cores() -> int:
    return len(os.sched_getaffinity(0)) if hasattr(os, "sched_getaffinity") else (os.cpu_count() or 1)


def machine_name() -> str:
    """CPU model of this runner, so tables measured on different shards can be told apart."""
    try:
        with open("/proc/cpuinfo", encoding="utf-8") as f:
            for line in f:
                if line.lower().startswith("model name"):
                    return line.split(":", 1)[1].strip()
    except OSError:
        pass
    try:
        out = subprocess.run(["lscpu"], capture_output=True, text=True, timeout=10, check=False).stdout
        for line in out.splitlines():
            if line.startswith("Model name:"):
                return line.split(":", 1)[1].strip()
    except (OSError, subprocess.SubprocessError):
        pass
    return "unknown CPU"


def auto_threads(cores: int) -> list[int]:
    counts = {1, cores, 2 * cores}
    n = 2
    while n < cores:
        counts.add(n)
        n *= 2
    return sorted(counts)


class BenchProcessError(Exception):
    """A benchmark process failed or timed out; carries the measurements it completed first."""

    def __init__(self, reason: str, completed: list[dict]):
        super().__init__(reason)
        self.completed = completed


def parse_lines(stdout: str | bytes | None) -> list[dict]:
    if isinstance(stdout, bytes):
        stdout = stdout.decode("utf-8", errors="replace")
    return [json.loads(line) for line in (stdout or "").splitlines() if line.startswith("{")]


def run_process(cmd: list[str], timeout: float) -> list[dict]:
    try:
        proc = subprocess.run(cmd, capture_output=True, text=True, timeout=timeout, check=False)
    except subprocess.TimeoutExpired as ex:
        raise BenchProcessError(f"timed out after {timeout:.0f}s", parse_lines(ex.stdout)) from ex
    if proc.returncode != 0:
        sys.stderr.write(proc.stderr)
        raise BenchProcessError(f"exit code {proc.returncode}", parse_lines(proc.stdout))
    return parse_lines(proc.stdout)


def collect(args: argparse.Namespace) -> tuple[list[dict], dict, list[str]]:
    # No measurement has run yet, so a failure here is a broken build: stop immediately.
    try:
        envs = {label: run_process([args.dotnet, str(dll), "env"], 60)[0] for label, dll in args.variants}
    except BenchProcessError as ex:
        raise SystemExit(f"cannot start the harness: {ex}") from ex
    candidate_label, candidate_dll = args.variants[0]
    meta = {
        "kind": "meta", "shard": args.shard, "machine": machine_name(), "envs": envs,
        "variants": [label for label, _ in args.variants], "trials": args.trials,
        "warmup_ms": args.warmup_ms, "rewarm_ms": args.rewarm_ms, "duration_ms": args.duration_ms,
        "size": args.size, "live": args.live, "seed": args.seed,
        "scenarios": args.scenario_list, "threads": args.threads_by_scenario,
    }

    jobs = []
    for scenario in args.scenario_list:
        jobs.append((scenario, "bcl", candidate_label, candidate_dll))
        jobs.extend((scenario, "nb", label, dll) for label, dll in args.variants)

    def process_seconds(scenario: str) -> float:
        n = len(args.threads_by_scenario[scenario])
        return (args.warmup_ms + (n - 1) * args.rewarm_ms + n * args.duration_ms) / 1000 + 5

    estimate = sum(process_seconds(sc) for sc, *_ in jobs) * args.trials
    print(f"shard {args.shard} on {meta['machine']}: {len(jobs)} jobs x {args.trials} trials; "
          f"estimated {estimate / 60:.0f} min", file=sys.stderr)

    rows: list[dict] = []
    failures: list[str] = []
    args.out.parent.mkdir(parents=True, exist_ok=True)
    with args.out.open("w", encoding="utf-8") as out:
        out.write(json.dumps(meta) + "\n")
        for trial in range(args.trials):
            order = jobs[:]
            random.Random(args.seed + trial).shuffle(order)
            for scenario, impl, label, dll in order:
                threads = args.threads_by_scenario[scenario]
                cmd = [args.dotnet, str(dll), "run", "--scenario", scenario, "--impl", impl,
                       "--threads", ",".join(map(str, threads)),
                       "--warmup-ms", str(args.warmup_ms), "--rewarm-ms", str(args.rewarm_ms),
                       "--duration-ms", str(args.duration_ms),
                       "--size", str(args.size), "--live", str(args.live), "--seed", str(args.seed)]
                started = time.monotonic()
                try:
                    measured = run_process(cmd, timeout=process_seconds(scenario) * 4 + 120)
                    status = ""
                except BenchProcessError as ex:
                    # One flaky process on a shared runner must not discard the whole matrix.
                    # Keep what it completed, record the gap, and fail the run at the end.
                    measured = ex.completed
                    status = f" FAILED ({ex})"
                    failure = (f"shard {args.shard}, trial {trial + 1}, {scenario}, {impl}@{label}: {ex}; "
                               f"{len(measured)} of {len(threads)} thread counts kept")
                    failures.append(failure)
                    out.write(json.dumps({"kind": "failure", "text": failure}) + "\n")
                for m in measured:
                    m.update(trial=trial, variant=label, series=f"{impl}@{label}" if impl == "nb" else "bcl")
                    rows.append(m)
                    out.write(json.dumps(m) + "\n")
                out.flush()
                print(f"trial {trial + 1}/{args.trials} {scenario:<16} {impl}@{label:<10} "
                      f"{time.monotonic() - started:5.1f}s{status}", file=sys.stderr)
    return rows, meta, failures


def load(files: list[Path]) -> tuple[list[dict], list[dict], list[str]]:
    """Read shard result files; reject shards whose settings differ, since they cannot share a summary."""
    rows, metas, failures = [], [], []
    for f in files:
        with f.open(encoding="utf-8") as fh:
            for line in fh:
                if not line.strip():
                    continue
                rec = json.loads(line)
                kind = rec.get("kind")
                if kind == "meta":
                    metas.append(rec)
                elif kind == "failure":
                    failures.append(rec["text"])
                else:
                    rows.append(rec)
    if not metas:
        raise SystemExit("--merge: no shard metadata found; were these files written by run_bench.py?")
    for m in metas[1:]:
        diff = [k for k in SHARED_SETTINGS if m[k] != metas[0][k]]
        if diff:
            raise SystemExit(f"--merge: shard {m['shard']} differs from shard {metas[0]['shard']} in {', '.join(diff)}")
    seen = [sc for m in metas for sc in m["scenarios"]]
    if len(seen) != len(set(seen)):
        raise SystemExit("--merge: a scenario appears in more than one shard")
    return rows, metas, failures


def permutation_p(a: list[float], b: list[float], rng: random.Random) -> float:
    """Two-sided permutation test on the difference of mean log rates."""
    la, lb = [math.log(x) for x in a], [math.log(x) for x in b]
    pooled, n = la + lb, len(la)
    observed = abs(statistics.fmean(la) - statistics.fmean(lb))
    total = sum(pooled)

    def stat(idx: tuple[int, ...]) -> float:
        sa = sum(pooled[i] for i in idx)
        return abs(sa / n - (total - sa) / (len(pooled) - n))

    if math.comb(len(pooled), n) <= PERMUTATION_LIMIT:
        splits = list(itertools.combinations(range(len(pooled)), n))
        hits = sum(1 for idx in splits if stat(idx) >= observed - 1e-12)
        return hits / len(splits)
    hits = sum(1 for _ in range(PERMUTATION_LIMIT)
               if stat(tuple(rng.sample(range(len(pooled)), n))) >= observed - 1e-12)
    return (hits + 1) / (PERMUTATION_LIMIT + 1)


def holm(pvalues: list[float]) -> list[float]:
    order = sorted(range(len(pvalues)), key=pvalues.__getitem__)
    adjusted, running = [0.0] * len(pvalues), 0.0
    for rank, i in enumerate(order):
        running = max(running, min(1.0, (len(pvalues) - rank) * pvalues[i]))
        adjusted[i] = running
    return adjusted


def fmt_ratio(ratio: float, p_adj: float) -> str:
    if p_adj < ALPHA:
        tag = "faster" if ratio > 1 else "slower"
    else:
        tag = "no clear difference"
    return f"{ratio:.2f}x (p={p_adj:.3f}) {tag}"


def fmt_rate(values: list[float]) -> str:
    q = statistics.quantiles(values, n=4) if len(values) >= 2 else [values[0]] * 3
    m = statistics.median(values)
    # Two decimals hide rates below 0.01 M/s; keep three significant digits for small values.
    f = (lambda v: f"{v / 1e6:.2f}") if m >= 1e6 else (lambda v: f"{v / 1e6:.3g}")
    return f"{f(m)} (IQR {f(q[0])}-{f(q[2])})"


def summarise(metas: list[dict], rows: list[dict], failures: list[str] | None = None,
              findings: list[dict] | None = None) -> str:
    """Markdown summary. When `findings` is given, candidate-vs-baseline results are appended to it."""
    cfg = metas[0]
    rng = random.Random(cfg["seed"])
    by = defaultdict(list)
    for r in rows:
        by[(r["scenario"], r["threads"], r["series"])].append(r)

    labels = cfg["variants"]
    cand = f"nb@{labels[0]}"
    base = f"nb@{labels[1]}" if len(labels) == 2 else None
    bcl = "bcl"
    shard_of = {sc: m for m in metas for sc in m["scenarios"]}
    threads_of = {sc: t for m in metas for sc, t in m["threads"].items()}
    scenario_list = [sc for sc in SCENARIOS if sc in shard_of]

    lines = ["# NonBlocking multi-core benchmark", ""]
    env = cfg["envs"][labels[0]]
    lines.append(f"Runtime {env['runtime']}, {env['arch']}, {env['processor_count']} logical CPUs, "
                 f"server GC {env['server_gc']}, {env['os']}.")
    machines = sorted({m["machine"] for m in metas})
    if len(metas) > 1:
        lines.append(f"{len(metas)} shards on separate runners ({'; '.join(machines)}). Each table comes "
                     "from one runner, and scenarios meant to be read together share one; absolute rates "
                     "are comparable only between tables from the same shard.")
    else:
        lines.append(f"CPU: {machines[0]}.")
    lines.append(f"{cfg['trials']} trials per series, warm-up {cfg['warmup_ms']} ms for the first thread count "
                 f"and {cfg['rewarm_ms']} ms for later ones, measured {cfg['duration_ms']} ms, "
                 f"size {cfg['size']:,}, churn live set {cfg['live']:,}. Rates are medians in millions per second.")
    lines.append("Ratios are ratios of medians. 'faster' or 'slower' only when an exact permutation test "
                 "rejects no-difference at 0.05 after Holm correction across thread counts within the table.")
    smallest_p = 2 / math.comb(2 * cfg["trials"], cfg["trials"])
    widest = max((len(t) for t in threads_of.values()), default=1)
    if smallest_p >= ALPHA / widest:
        lines.append(f"**Too few trials:** with {cfg['trials']} trials the smallest attainable p-value is "
                     f"{smallest_p:.3f}, so no row can be called faster or slower. Use at least 6.")
    if failures:
        lines.append(f"**{len(failures)} benchmark process(es) failed;** their series have fewer trials "
                     "and rows with no data are omitted:")
        lines.extend(f"- {f}" for f in failures)
    lines.append("")

    for scenario in scenario_list:
        unit = UNITS.get(scenario, "M ops/s")
        lines.append(f"## {scenario} ({unit})")
        lines.append("")
        if len(metas) > 1:
            lines.append(f"Shard {shard_of[scenario]['shard']}, {shard_of[scenario]['machine']}.")
            lines.append("")
        header = ["threads", cand]
        if base:
            header += [base, f"{labels[0]} / {labels[1]}"]
        header += ["bcl", f"{cand} / bcl"]
        if base:
            header += [f"{base} / bcl"]
        lines.append("| " + " | ".join(header) + " |")
        lines.append("|" + "---|" * len(header))

        rows_out, notes = [], []
        present = [s for s in (cand, base, bcl) if s]
        # Each column is its own Holm family: (numerator, denominator) series.
        pairs = {"base": (cand, base), "bcl": (cand, bcl), "base_bcl": (base, bcl)}
        comparisons = {key: [] for key, (num, den) in pairs.items() if num and den}
        for threads in threads_of[scenario]:
            series = {s: [r["ops_per_sec"] for r in by[(scenario, threads, s)]] for s in present}
            if not all(series.values()):
                continue
            row = {"threads": threads, "series": series}
            for key in comparisons:
                num, den = pairs[key]
                ratio = statistics.median(series[num]) / statistics.median(series[den])
                comparisons[key].append((row, ratio, permutation_p(series[num], series[den], rng)))
            rows_out.append(row)
            notes.extend(scenario_notes(scenario, threads, by, present))

        for key, items in comparisons.items():
            for (row, ratio, _), p_adj in zip(items, holm([p for _, _, p in items])):
                row[key] = fmt_ratio(ratio, p_adj)
                if key == "base" and findings is not None:
                    findings.append({"scenario": scenario, "threads": row["threads"], "ratio": ratio,
                                     "p": p_adj, "notes": scenario_metrics(scenario, row["threads"], by, cand, base)})

        for row in rows_out:
            series = row["series"]
            cells = [str(row["threads"]), fmt_rate(series[cand])]
            if base:
                cells += [fmt_rate(series[base]), row["base"]]
            cells += [fmt_rate(series[bcl]), row["bcl"]]
            if base:
                cells += [row["base_bcl"]]
            lines.append("| " + " | ".join(cells) + " |")
        lines.append("")
        if notes:
            lines.extend(notes)
            lines.append("")

    return "\n".join(lines)


def scenario_notes(scenario: str, threads: int, by: dict, series: list[str]) -> list[str]:
    notes = []
    for s in series:
        rs = by[(scenario, threads, s)]
        if scenario.startswith(("get-", "miss-", "small-")):
            ratio = statistics.median(r["extra"]["aux_per_sec"] / r["ops_per_sec"] for r in rs)
            expected = rs[0]["extra"].get("expected_hit_ratio", 1.0)
            # Lookups pick keys at random, so a mixed hit/miss ratio only matches in expectation.
            if abs(ratio - expected) > (0.001 if expected == 1.0 else 0.02):
                notes.append(f"- WARNING {s} at {threads} threads: hit ratio {ratio:.3f}, expected {expected:.3f}")
            if threads == min(t for (sc, t, _) in by if sc == scenario):
                mb = statistics.median(r["extra"]["table_mb"] for r in rs)
                notes.append(f"- {s}: filled map holds {mb:.1f} MB")
        elif scenario in ("churn-str", "churn-long", "churn-burst", "readd-str"):
            mb = statistics.median(r["extra"]["retained_mb"] for r in rs)
            en = statistics.median(r["extra"]["post_churn_enumerate_ms"] for r in rs)
            notes.append(f"- {s} at {threads} threads: retained {mb:.1f} MB growth over the run, "
                         f"one enumeration afterwards {en:.2f} ms")
        elif scenario in ("insert-long-mirrored", "grow-int", "grow-str"):
            if threads == min(t for (sc, t, _) in by if sc == scenario):
                mb = statistics.median(r["extra"]["table_mb"] for r in rs)
                n = int(rs[0]["extra"]["keys_per_table"])
                notes.append(f"- {s}: one filled table of {n:,} keys holds {mb:.2f} MB")
        elif scenario == "enum-write" and threads > 1:
            w = statistics.median(r["extra"]["aux_per_sec"] for r in rs) / 1e6
            notes.append(f"- {s} at {threads} threads: writers sustained {w:.2f} M ops/s")
    return notes


def scenario_metrics(scenario: str, threads: int, by: dict, cand: str, base: str) -> str:
    """Side metrics worth reading next to a throughput change, as 'name head vs base'."""
    def med(series: str, key: str) -> float | None:
        rs = by[(scenario, threads, series)]
        vals = [r["extra"][key] for r in rs if key in r.get("extra", {})]
        return statistics.median(vals) if vals else None

    parts = []
    for key, label, fmt in (("retained_mb", "retained", "{:.1f} MB"),
                            ("post_churn_enumerate_ms", "enumeration after", "{:.2f} ms"),
                            ("table_mb", "table", "{:.1f} MB")):
        h, b = med(cand, key), med(base, key)
        if h is not None and b is not None:
            parts.append(f"{label} {fmt.format(h)} vs {fmt.format(b)}")
    return "; ".join(parts)


# GitHub Actions caps the annotations it shows per step, so the most informative ones go first.
MAX_ANNOTATIONS = 10


def _escape_data(text: str) -> str:
    return text.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")


def _escape_property(text: str) -> str:
    return _escape_data(text).replace(":", "%3A").replace(",", "%2C")


def annotations(findings: list[dict], label: str, cand: str, base: str) -> list[str]:
    """One ::notice (faster) or ::warning (slower) per table with a significant candidate-vs-baseline
    row, plus a summary line. Warnings sort first, then larger effects."""
    by_scenario: dict[str, list[dict]] = defaultdict(list)
    for f in findings:
        by_scenario[f["scenario"]].append(f)

    lines, faster, slower, unchanged = [], [], [], []
    for scenario in (sc for sc in SCENARIOS if sc in by_scenario):
        sig = [f for f in by_scenario[scenario] if f["p"] < ALPHA]
        if not sig:
            unchanged.append(scenario)
            continue
        # Same rule as fmt_ratio: a significant ratio of exactly 1 is reported as slower.
        worse = [f for f in sig if f["ratio"] <= 1]
        group, kind = (worse, "warning") if worse else (sig, "notice")
        (slower if worse else faster).append(scenario)
        ratios = [f["ratio"] for f in group]
        span = f"{min(ratios):.2f}x" if len(ratios) == 1 else f"{min(ratios):.2f}-{max(ratios):.2f}x"
        at = ", ".join(str(f["threads"]) for f in group)
        top = max(group, key=lambda f: f["threads"])
        detail = f"{cand} / {base} {span} at {at} threads"
        if top["notes"]:
            detail += f" ({top['threads']} threads: {top['notes']})"
        verdict = "slower" if worse else "faster"
        lines.append((0 if worse else 1, -abs(math.log(min(ratios) if worse else max(ratios))),
                      f"::{kind} title={_escape_property(f'{label} {scenario}: {verdict}')}::{_escape_data(detail)}"))

    lines.sort(key=lambda t: (t[0], t[1]))
    summary = (f"{len(faster)} faster, {len(slower)} slower, {len(unchanged)} unchanged tables"
               + (f"; slower: {', '.join(slower)}" if slower else "")
               + (f"; faster: {', '.join(faster)}" if faster else ""))
    head = f"::{'warning' if slower else 'notice'} title={_escape_property(f'{label}: {cand} vs {base}')}::{_escape_data(summary)}"
    return [head] + [text for _, _, text in lines[:MAX_ANNOTATIONS - 1]]


def main() -> int:
    args = parse_args()
    if args.merge:
        rows, metas, failures = load(args.merge)
    else:
        rows, meta, failures = collect(args)
        metas = [meta]
    findings: list[dict] = []
    summary = summarise(metas, rows, failures, findings)
    args.summary.parent.mkdir(parents=True, exist_ok=True)
    args.summary.write_text(summary + "\n", encoding="utf-8")
    if args.annotate:
        labels = metas[0]["variants"]
        if len(labels) == 2:
            print("\n".join(annotations(findings, args.annotate, f"nb@{labels[0]}", f"nb@{labels[1]}")))
        else:
            print(f"::notice title={_escape_property(args.annotate)}::no baseline in this run; see the step summary")
    else:
        print(summary)
    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(main())
