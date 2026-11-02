"""Frame cost of the Snitch overlay in a running test slot: opening it, and dragging a window.

Usage: python -I tests/overlay-perf.py [--slot mptest2] [--label before] [--open-max-ms 50] [--drag-p95-ms 40]

Open: the overlay is closed, then opened with 'snitch open' (the same SetMode path as the F6 key) while the
bridge records frame times. The worst frame of that window is the hitch the player sees on F6. Measured three
times, because before the pre-build every open mounted the page again.

Drag: the expanded overlay is open and a mod window is dragged by its title bar over 120 frames with
'sideloaddrag' (DEBUG Sideload) while frame times are recorded. Also counts Sideload's computed-tree dumps and
'layout saved' lines written during the drag.

Prints one result line per measurement and PASS/FAIL against the thresholds; exit 1 when any failed.
"""
import argparse
import json
import os
import re
import subprocess
import sys
import threading
import time

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
S1 = os.path.join(ROOT, "Workspace", "tools", "bridge", "s1.py")
APP = "snitch-overlay"
results = []


def s1(slot, *args, timeout=60):
    cmd = [sys.executable, "-I", S1] + (["--slot", slot] if slot else []) + list(args)
    p = subprocess.run(cmd, capture_output=True, text=True, timeout=timeout)
    return p.returncode, (p.stdout or "") + (p.stderr or "")


def check(name, ok, detail=""):
    results.append(ok)
    print(("PASS " if ok else "FAIL ") + name + ("  - " + detail if detail else ""))


def frames_during(slot, seconds, action, delay=0.4):
    """Record frame stats for `seconds` while `action` runs after `delay`. Returns (stats dict or None, raw)."""
    box = {}

    def rec():
        box["out"] = s1(slot, "frames", str(seconds), timeout=seconds + 30)

    t = threading.Thread(target=rec)
    t.start()
    time.sleep(delay)
    action()
    t.join()
    code, out = box.get("out", (1, "no output"))
    m = re.search(r"\{.*\}", out, re.S)
    try:
        return (json.loads(m.group(0)) if m else None), out
    except ValueError as e:
        print("could not parse frame stats: " + str(e))
        return None, out


def worst(stats):
    for k in ("maxMs", "max_ms", "max"):
        if stats and k in stats:
            return float(stats[k])
    return -1.0


def pick(stats, *keys):
    for k in keys:
        if stats and k in stats:
            return float(stats[k])
    return -1.0


def log_count(slot, pattern, n=4000):
    code, out = s1(slot, "log", "-n", str(n), "--grep", pattern)
    return len([l for l in out.splitlines() if pattern in l])


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--slot", default=None)
    ap.add_argument("--label", default="run")
    ap.add_argument("--open-max-ms", type=float, default=50.0)
    ap.add_argument("--drag-p95-ms", type=float, default=40.0)
    a = ap.parse_args()
    slot = a.slot

    s1(slot, "run", "--until", "(?i)sampl|start", "--max-wait", "8000", "snitch", "start")
    s1(slot, "run", "--until", r"\[overlay\] (hidden|already hidden)", "--max-wait", "8000", "snitch", "close")
    time.sleep(1.0)

    idle, raw = frames_during(slot, 2, lambda: None)
    print(f"[{a.label}] idle: {json.dumps(idle)}")

    opens = []
    switch = []
    for i in range(3):
        st, raw = frames_during(slot, 3, lambda: s1(slot, "console", "snitch", "open"))
        w = worst(st)
        opens.append(w)
        time.sleep(0.3)
        code, log = s1(slot, "log", "-n", "400", "--grep", "open cost")
        lines = [l for l in log.splitlines() if "open cost" in l]
        m = re.search(r"switch-on (\d+(?:\.\d+)?) ms", lines[-1]) if lines else None
        switch.append(float(m.group(1)) if m else -1.0)
        print(f"[{a.label}] open #{i + 1}: worst frame in 3 s {w:.1f} ms; host: "
              f"{lines[-1].split('open cost: ')[-1] if lines else 'no open cost line (old build)'}")
        time.sleep(0.5)
        s1(slot, "run", "--until", r"\[overlay\] (hidden|already hidden)", "--max-wait", "8000", "snitch", "close")
        time.sleep(1.0)
    # The 3 s window also holds the page's regular rebuilds for new numbers (4 per second while shown), so the
    # check uses the host's switch-on figure: the frames between the key and the first state push.
    check(f"open: switch-on of every open <= {a.open_max_ms:.0f} ms", all(0 <= w <= a.open_max_ms for w in switch),
          "switch-on=" + ",".join(f"{w:.1f}" for w in switch) + " worst-in-3s=" + ",".join(f"{w:.1f}" for w in opens))

    s1(slot, "run", "--until", r"\[overlay\] (expanded|already expanded)", "--max-wait", "8000", "snitch", "open", "all")
    s1(slot, "run", "--until", "layout reset", "--max-wait", "8000", "snitch", "layout", "reset")
    time.sleep(1.5)
    shown, raw = frames_during(slot, 2, lambda: None)
    print(f"[{a.label}] expanded, standing still (no check): {json.dumps(shown)}")
    code, out = s1(slot, "eval", APP, "(function () { var e = document.querySelector('.mwin'); return e ? e.id : ''; })()")
    lines = [l.strip().strip('"') for l in out.splitlines() if l.strip()]
    eid = lines[-1] if lines else ""
    if not re.fullmatch(r"\w+-\d+", eid):
        check("drag: a mod window exists", False, out)
    else:
        n = eid.split("-")[1]
        dumps0 = log_count(slot, "computed tree")
        saved0 = log_count(slot, "layout saved")
        released = {}

        def go():
            code, out = s1(slot, "run", "--until", "drag on .* released", "--max-wait", "30000", "--",
                           "sideloaddrag", APP, "0.2,0.5", "0.6,3", "120", f"#bar-{n}", timeout=60)
            released["ok"] = re.search("drag on .* released", out) is not None

        # The frames are recorded inside the drag only (it lasts about 3 s): the drag starts first, the recording
        # 0.3 s later for 2 s, so the state pushes before and after the drag are not counted as drag cost.
        t = threading.Thread(target=go)
        t.start()
        time.sleep(0.3)
        code, raw = s1(slot, "frames", "2", timeout=40)
        m = re.search(r"\{.*\}", raw, re.S)
        try:
            st = json.loads(m.group(0)) if m else None
        except ValueError as e:
            print("could not parse frame stats: " + str(e))
            st = None
        t.join()
        if not released.get("ok"):
            print(f"[{a.label}] the drag did not report its release")
        time.sleep(1.0)
        dumps = log_count(slot, "computed tree") - dumps0
        saved = log_count(slot, "layout saved") - saved0
        p95 = pick(st, "p95Ms", "p95_ms", "p95")
        mean = pick(st, "meanMs", "avgMs", "mean_ms", "avg")
        print(f"[{a.label}] drag 120 frames: mean {mean:.1f} ms p95 {p95:.1f} ms worst {worst(st):.1f} ms, "
              f"tree dumps {dumps}, layout saved {saved}  stats={json.dumps(st)}")
        check(f"drag: p95 frame <= {a.drag_p95_ms:.0f} ms", 0 <= p95 <= a.drag_p95_ms, f"p95={p95:.1f}")
        check("drag: 'layout saved' exactly once (on release)", saved == 1, f"saved={saved}")
        s1(slot, "run", "--until", "layout reset", "--max-wait", "8000", "snitch", "layout", "reset")

    s1(slot, "run", "--until", r"\[overlay\] (hidden|already hidden)", "--max-wait", "8000", "snitch", "close")
    failed = results.count(False)
    print(f"{len(results) - failed}/{len(results)} PASS")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
