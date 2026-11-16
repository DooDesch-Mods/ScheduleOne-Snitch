"""Measures what a slot's frames spend and what its stutters are, for comparing one mod set against another.

Usage: python -I tests/measure-stutters.py --slot mptest3 --label main [--seconds 60] [--patches] [--out DIR]
Starts sampling, lets the windows fill, then records for --seconds: frame stats, the engine work by kind, the
stutters with their cause, and the top sections. With --patches it then switches on Harmony patch timing and
records a second window, so the cost of other mods' patches shows under their mod. Everything goes to
<out>/<label>.txt; the script prints the summary lines. A measurement, not a check: it always exits 0 unless the
bridge fails.
"""
import argparse
import os
import re
import subprocess
import sys
import time

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
S1 = os.path.join(ROOT, "Workspace", "tools", "bridge", "s1.py")


def s1(slot, *args, timeout=180):
    cmd = [sys.executable, "-I", S1, "--slot", slot] + list(args)
    p = subprocess.run(cmd, capture_output=True, text=True, timeout=timeout)
    return p.returncode, (p.stdout or "") + (p.stderr or "")


def run(slot, until, *words, wait=15000):
    return s1(slot, "run", "--until", until, "--max-wait", str(wait), "--", *words)


def window(slot, seconds, title, out):
    out.append(f"===== {title}")
    code, f = s1(slot, "frames", str(seconds), timeout=seconds + 60)
    out.append(f"frames ({code}): {f.strip()}")
    for words, until in ((["snitch", "frame"], r"(?i)frame|ms"),
                         (["snitch", "engine", "15"], r"stutters in the last"),
                         (["snitch", "top", "25"], r"(?i)top|ms"),
                         (["snitch", "unattributed"], r"(?i)unattributed")):
        # The listing goes on after its header, so the command collects for a fixed time instead of stopping at a match.
        code, o = run(slot, r"(?!x)x", *words, wait=3000)
        out.append(f"----- {' '.join(words)} ({code})")
        out.append(o.strip())


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--slot", required=True)
    ap.add_argument("--label", required=True)
    ap.add_argument("--seconds", type=int, default=60)
    ap.add_argument("--patches", action="store_true")
    ap.add_argument("--out", default=os.path.join(os.getcwd(), "measure"))
    a = ap.parse_args()
    os.makedirs(a.out, exist_ok=True)
    out = []

    code, o = run(a.slot, r"(?i)sampl", "snitch", "start")
    out.append(f"snitch start ({code}): {o.strip()[-300:]}")
    time.sleep(10.0)
    window(a.slot, a.seconds, f"{a.label}: sampling", out)

    if a.patches:
        t0 = time.time()
        code, o = run(a.slot, r"(?i)wrapp|patch", "snitch", "patches", "on")
        out.append(f"===== snitch patches on ({code}, {time.time() - t0:.1f} s): {o.strip()}")
        time.sleep(5.0)
        window(a.slot, a.seconds, f"{a.label}: with patch timing", out)
        code, o = run(a.slot, r"(?i)patch|wrapp", "snitch", "patches", "list")
        out.append(f"----- snitch patches list ({code})")
        out.append(o.strip())

    path = os.path.join(a.out, a.label + ".txt")
    with open(path, "w", encoding="utf-8") as fh:
        fh.write("\n".join(out) + "\n")
    text = "\n".join(out)
    for line in text.splitlines():
        if re.search(r"avgMs|\"p50Ms\"|cause |by kind|^\s+(Scripts|Rendering|Physics|Coroutines|Waiting|Animation|NPC|UI|Loading|Audio)", line):
            print(line.strip()[:200])
    print("written: " + path)
    return 0


if __name__ == "__main__":
    sys.exit(main())
