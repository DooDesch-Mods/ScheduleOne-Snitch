"""Frame cost of the Snitch overlay per mode in a running test slot.

Usage: python -I tests/frame-cost.py [--slot mptest6] [--rounds 3] [--seconds 6] [--budget-ms 2.0]
Alternates hidden, compact and expanded for several rounds (other games on the machine move the absolute numbers,
so only the difference to hidden counts) and prints the mean frame time, p95 and worst frame of each mode. PASS
when expanded costs at most --budget-ms more than hidden on average and the worst expanded frame is not more than
16 ms above the worst hidden frame. Exit code 1 on any FAIL.
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
    return json.loads(m.group(0)) if code == 0 and m else None


def mode(slot, words, until):
    code, out = s1(slot, "run", "--until", until, "--max-wait", "8000", "--", *words.split())
    return code == 0 and re.search(until, out) is not None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--slot", default=None)
    ap.add_argument("--rounds", type=int, default=3)
    ap.add_argument("--seconds", type=int, default=6)
    ap.add_argument("--budget-ms", type=float, default=2.0)
    a = ap.parse_args()

    mode(a.slot, "snitch start", r"sampl")
    steps = [("hidden", "snitch close", r"\[overlay\] (hidden|already hidden)"),
             ("compact", "snitch open", r"\[overlay\] (compact|already compact)"),
             ("expanded", "snitch open all", r"\[overlay\] (expanded|already expanded)")]
    data = {n: [] for n, _, _ in steps}
    for r in range(a.rounds):
        for name, cmd, until in steps:
            if not mode(a.slot, cmd, until):
                print(f"FAIL could not switch to {name}")
                return 1
            time.sleep(2.0)  # the first page build and font load of a mode are not steady state
            f = frames(a.slot, a.seconds)
            if not f:
                print(f"FAIL no frame stats for {name}")
                return 1
            data[name].append(f)
            print(f"round {r + 1} {name:9s} avg {f['avgMs']:.2f} ms  p95 {f['p95Ms']:.2f}  p99 {f['p99Ms']:.2f}  max {f['maxMs']:.2f}  ({f['frames']} frames)")
    mode(a.slot, "snitch close", r"\[overlay\] (hidden|already hidden)")

    def mean(name, key):
        return sum(f[key] for f in data[name]) / len(data[name])

    for name, _, _ in steps:
        print(f"mean {name:9s} avg {mean(name, 'avgMs'):.2f} ms  p95 {mean(name, 'p95Ms'):.2f}  max {max(f['maxMs'] for f in data[name]):.2f}")
    extra = mean("expanded", "avgMs") - mean("hidden", "avgMs")
    extra_c = mean("compact", "avgMs") - mean("hidden", "avgMs")
    spike = max(f["maxMs"] for f in data["expanded"]) - max(f["maxMs"] for f in data["hidden"])
    ok1 = extra <= a.budget_ms
    ok2 = spike <= 16.0
    print(f"{'PASS' if ok1 else 'FAIL'} expanded costs {extra:.2f} ms over hidden on average (budget {a.budget_ms:.1f} ms; compact {extra_c:.2f} ms)")
    print(f"{'PASS' if ok2 else 'FAIL'} worst expanded frame is {spike:.1f} ms above the worst hidden frame (limit 16 ms)")
    return 0 if ok1 and ok2 else 1


if __name__ == "__main__":
    sys.exit(main())
