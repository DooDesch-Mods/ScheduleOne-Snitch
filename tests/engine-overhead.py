"""Frame cost of Snitch sampling (sections, engine step timing, stutter log) in a running test slot.

Usage: python -I tests/engine-overhead.py [--slot mptest3] [--rounds 5] [--seconds 6] [--budget-ms 0.5]
Alternates 'snitch stop' and 'snitch start' for several rounds with the overlay hidden and prints median, mean, p95
and worst frame of each state. Hitches from other processes swing the mean by several ms, so the check compares the
median frame of each round and takes the median over the rounds. PASS when sampling costs at most --budget-ms more
than stopped. Exit code 1 on any FAIL.
"""
import argparse
import json
import os
import re
import subprocess
import sys
import time

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
S1 = os.path.join(ROOT, "Workspace", "tools", "bridge", "s1.py")


def s1(slot, *args, timeout=120):
    cmd = [sys.executable, "-I", S1] + (["--slot", slot] if slot else []) + list(args)
    p = subprocess.run(cmd, capture_output=True, text=True, timeout=timeout)
    return p.returncode, (p.stdout or "") + (p.stderr or "")


def frames(slot, seconds):
    code, out = s1(slot, "frames", str(seconds))
    m = re.search(r"\{.*\}", out, re.S)
    if code != 0 or not m:
        print(f"no frame stats (exit {code}): {out.strip()[-300:]}")
        return None
    return json.loads(m.group(0))


def run(slot, words, until):
    code, out = s1(slot, "run", "--until", until, "--max-wait", "8000", "--", *words.split())
    ok = code == 0 and re.search(until, out) is not None
    if not ok:
        print(f"'{words}' did not log /{until}/ (exit {code}): {out.strip()[-300:]}")
    return ok


def median(values):
    v = sorted(values)
    n = len(v)
    return v[n // 2] if n % 2 else (v[n // 2 - 1] + v[n // 2]) / 2


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--slot", default=None)
    ap.add_argument("--rounds", type=int, default=5)
    ap.add_argument("--seconds", type=int, default=6)
    ap.add_argument("--budget-ms", type=float, default=0.5)
    a = ap.parse_args()

    run(a.slot, "snitch close", r"\[overlay\] (hidden|already hidden)")
    steps = [("stopped", "snitch stop", r"sampling stopped|not sampling|idle"),
             ("sampling", "snitch start", r"sampling started|already sampling")]
    data = {n: [] for n, _, _ in steps}
    for r in range(a.rounds):
        for name, cmd, until in steps:
            if not run(a.slot, cmd, until):
                print(f"FAIL could not switch to {name}")
                return 1
            time.sleep(2.0)  # the windows refill after a start; not steady state yet
            f = frames(a.slot, a.seconds)
            if not f:
                print(f"FAIL no frame stats for {name}")
                return 1
            data[name].append(f)
            print(f"round {r + 1} {name:8s} p50 {f['p50Ms']:.2f} ms  avg {f['avgMs']:.2f}  p95 {f['p95Ms']:.2f}  max {f['maxMs']:.2f}  ({f['frames']} frames)")

    for name, _, _ in steps:
        rows = data[name]
        print(f"{name:8s} median p50 {median(f['p50Ms'] for f in rows):.2f} ms  "
              f"mean avg {sum(f['avgMs'] for f in rows) / len(rows):.2f}  max {max(f['maxMs'] for f in rows):.2f}")
    extra = median(f["p50Ms"] for f in data["sampling"]) - median(f["p50Ms"] for f in data["stopped"])
    ok = extra <= a.budget_ms
    print(f"{'PASS' if ok else 'FAIL'} sampling costs {extra:.2f} ms per median frame over stopped (budget {a.budget_ms:.2f} ms)")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
