"""Stutter attribution check: makes stutters with a known cause in a running test slot and checks that Snitch names
exactly that cause.

Usage: python -I tests/spike-attribution.py [--slot mptest3] [--deep]
--deep runs only the deep-probe case; run it after every other check of a pass.
Needs a loaded save with a Debug build of Snitch (the 'snitch burn' command) and the ScheduleMCP bridge.
Each case arms one stutter, then reads the '[snitch] stutter' log line that follows the matching 'burn:' line
(its 'cause:' and 'mod:') and the compact overlay's "Last stutter:" line. Also checks that the game's time is the
Game block below the mods. Prints PASS/FAIL per check; exit code 1 when any check failed.

Cases (the test kit's code is timed under the group "SnitchTest", as if it were another mod):
  burn 80            spun outside every section     -> cause "Scripts" (the engine kind of the Update step)
  burn 80 section    spun inside SnitchTest.Burn    -> cause SnitchTest.Burn, mod SnitchTest
  burn 80 patch      spun inside a Harmony prefix   -> cause SnitchTest.SpikePatch.Prefix, mod SnitchTest
  burn 80 behaviour  spun in an injected MonoBehaviour's Update -> SnitchTest.SpikeBehaviour.Update
  burn 80 coroutine  spun in a MelonCoroutines coroutine -> SnitchTest.SpikeKit.BurnRoutine (coroutine)
  burn gc            a forced game collection       -> cause "Garbage collection", frame marked GC
  burn garbage       allocations in SnitchTest.Alloc, then a game collection -> mod SnitchTest
  burn garbage net   the same on the .NET heap      -> cause ".NET garbage collection", mod SnitchTest
  burn 80 game       spun in the game's MonoBehaviourEvents.Update, deep probes off -> cause "Scripts" and the hint
                     "'snitch deep on' names the class"
  (deep on) burn 80 game  the same with deep probes on -> cause Game.MonoBehaviourEvents.Update, class
                     MonoBehaviourEvents.Update, no hint. Runs last: the deep probes stay in until the game restarts.
"""
import argparse
import os
import re
import subprocess
import sys
import time
from datetime import datetime

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
S1 = os.path.join(ROOT, "Workspace", "tools", "bridge", "s1.py")
PREFIX = "[snitch] stutter "

results = []


def s1(slot, *args, timeout=60):
    cmd = [sys.executable, "-I", S1] + (["--slot", slot] if slot else []) + list(args)
    p = subprocess.run(cmd, capture_output=True, text=True, timeout=timeout)
    return p.returncode, (p.stdout or "") + (p.stderr or "")


def check(name, ok, detail=""):
    results.append(ok)
    print(("PASS " if ok else "FAIL ") + name + ("  - " + detail.strip().replace("\n", " | ")[:400] if detail else ""))


def stamp(line):
    m = re.search(r"\[(\d\d:\d\d:\d\d\.\d{3})\]", line)
    return datetime.strptime(m.group(1), "%H:%M:%S.%f") if m else None


def log_lines(slot, n=6000):
    _, out = s1(slot, "log", "-n", str(n))
    return out.splitlines()


def snitch_lines(slot):
    return [l for l in log_lines(slot) if "[snitch] burn:" in l or PREFIX in l]


def stutter_after_burn(slot, burn_marker, window=1.5):
    """The first stutter line logged within `window` s after the newest burn line containing burn_marker."""
    lines = snitch_lines(slot)
    burns = [l for l in lines if "burn:" in l and burn_marker in l]
    if not burns:
        return None, "no burn line containing '" + burn_marker + "' in the log"
    t0 = stamp(burns[-1])
    for l in lines:
        t = stamp(l)
        if t and PREFIX in l and 0 <= (t - t0).total_seconds() <= window:
            return l, ""
    return None, "burn at " + burns[-1][:40] + f" but no stutter line within {window} s"


def field(line, name):
    m = re.search(r"\| " + name + r": (.+?)(?= \||$)", line)
    return m.group(1).strip() if m else ""


def cause_of(line):
    m = re.search(r"cause: (.+?) \+([\d.]+)", line)
    return m.group(1) if m else ""


def overlay_stutter(slot):
    """The overlay's stutter line and its per-cause counts line as (last, counts); '' when hidden or missing (the
    bridge answers None for an empty value)."""
    code, out = s1(slot, "eval", "snitch-overlay",
                   "(function () { var e = document.getElementById('stut'), h = document.getElementById('hint'); "
                   "return (e && e.style.display !== 'none' ? e.textContent : '') + ' ||| ' + (h ? h.textContent : ''); })()")
    v = out.strip().splitlines()[-1].strip() if code == 0 and out.strip() else ""
    if v == "None" or " ||| " not in v:
        return "", ""
    last, counts = v.split(" ||| ", 1)
    return last, counts


def overlay_poll(slot, expect, timeout=4.0):
    """Read the overlay right after a burn is armed until it names `expect`. A natural stutter (a Debug Sideload makes
    about one a second) soon replaces the burn as the newest one, so the per-cause counts of the last 30 s count as
    well. Returns (how, last, counts); how is '' when the overlay never named it."""
    last = counts = ""
    end = time.time() + timeout
    while time.time() < end:
        last, counts = overlay_stutter(slot)
        if expect in last and last.startswith("Last stutter: "):
            return "last stutter", last, counts
        if expect in counts and "stutter" in counts:
            return "stutter counts", last, counts
        time.sleep(0.1)
    return "", last, counts


def overlay_check(words, expect, seen):
    how, last, counts = seen
    check(f"{words}: overlay names {expect} ('Last stutter' line or the stutter counts)", how != "",
          f"via={how or '-'} last='{last}' counts='{counts[:160]}'")


def overlay_shows(slot, words, expect, timeout=4.0):
    overlay_check(words, expect, overlay_poll(slot, expect, timeout))


def command_lines(slot, words, header, wait=8.0):
    """Run a console command and return its own lines from the game log, from the newest line matching `header`
    on. The bridge's 'run' answer holds at most 80 lines, which a Debug Sideload's layout dump can fill first."""
    s1(slot, "run", "--until", header, "--max-wait", "8000", *words.split())
    pat = re.compile(header)
    end = time.time() + wait
    while True:
        lines = log_lines(slot, 3000)
        idx = [i for i, l in enumerate(lines) if pat.search(l)]
        if idx:
            return [l for l in lines[idx[-1]:] if "[Snitch]" in l][:200]
        if time.time() >= end:
            return []
        time.sleep(0.5)


def case(slot, words, burn_marker, expect_cause, expect_mod=None, expect_gc=False):
    # A natural stutter can take the 1.5 s window or replace the burn on the overlay before it is read, so the burn
    # is tried again until both name it.
    line, why, seen = None, "", ("", "", "")
    for attempt in range(3):
        code, out = s1(slot, "run", "--until", "armed", "--max-wait", "8000", *words.split())
        if code != 0 or "armed" not in out:
            check("console: " + words, False, out)
            return
        got = overlay_poll(slot, expect_cause)
        if got[0]:
            seen = got
        elif not seen[0]:
            seen = got
        time.sleep(0.5)
        line, why = stutter_after_burn(slot, burn_marker)
        if line is not None and cause_of(line) == expect_cause and seen[0]:
            break
        time.sleep(2.0)
    overlay_check(words, expect_cause, seen)
    if line is None:
        check(words + ": a stutter is logged", False, why)
        return
    check(words + ": a stutter is logged", True, line[line.find(PREFIX):][:200])
    cause = cause_of(line)
    check(f"{words}: cause is {expect_cause}", cause == expect_cause, f"cause='{cause}'")
    if expect_mod is not None:
        mod = field(line, "mod")
        check(f"{words}: mod is {expect_mod}", mod == expect_mod, f"mod='{mod}'")
    if expect_gc:
        check(f"{words}: frame is marked GC", " GC x" in line, line[:200])


HINT = "'snitch deep on' names the class"
GAME_MARKER = "inside the game script MonoBehaviourEvents.Update"


def game_case(slot, deep):
    """A burn inside the game's own script. Deep off: the engine kind plus the hint. Deep on: the class."""
    words = "snitch burn 80 game" + (" (deep on)" if deep else " (deep off)")
    expect_cause = "Game.MonoBehaviourEvents.Update" if deep else "Scripts"
    line, why, seen = None, "", ("", "", "")
    expect_overlay = "MonoBehaviourEvents.Update" if deep else "Scripts"
    # A natural stutter can take the 1.5 s window or replace the burn on the overlay before it is read, so the burn
    # is tried again until both name it.
    for attempt in range(3):
        code, out = s1(slot, "run", "--until", "armed|could not", "--max-wait", "8000", "snitch", "burn", "80", "game")
        if code != 0 or "armed" not in out:
            check("console: " + words, False, out)
            return
        got = overlay_poll(slot, expect_overlay)
        if got[0] or not seen[0]:
            seen = got
        time.sleep(0.5)
        line, why = stutter_after_burn(slot, GAME_MARKER)
        if line is not None and cause_of(line) == expect_cause and seen[0]:
            break
        time.sleep(2.0)
    overlay_check(words, expect_overlay, seen)
    if line is None:
        check(words + ": a stutter is logged", False, why)
        return
    check(words + ": a stutter is logged", True, line[line.find(PREFIX):][:220])
    cause = cause_of(line)
    check(f"{words}: cause is {expect_cause}", cause == expect_cause, f"cause='{cause}'")
    if deep:
        cls = field(line, "class")
        check(f"{words}: class is MonoBehaviourEvents.Update", cls == "MonoBehaviourEvents.Update", f"class='{cls}'")
        check(f"{words}: no deep hint once the class is known", HINT not in line, line[line.find("cause:"):][:200])
    else:
        check(f"{words}: the line carries the hint \"{HINT}\"", HINT in line, line[line.find("cause:"):][:200])
        check(f"{words}: no mod is blamed", field(line, "mod") == "", f"mod='{field(line, 'mod')}'")


def wait_deep(slot, timeout=300):
    """Wait for the deep probes' final 'wrapped N game methods' line; returns (wrapped, failed) or None."""
    pat = re.compile(r"deep probes: wrapped (\d+) game methods .*?, (\d+) failed")
    end = time.time() + timeout
    while time.time() < end:
        hits = [pat.search(l) for l in log_lines(slot, 4000)]
        hits = [h for h in hits if h]
        if hits:
            return int(hits[-1].group(1)), int(hits[-1].group(2))
        time.sleep(3.0)
    return None


def wait_wrapped(slot, name, timeout=60):
    """Wait for the '[snitch] <name>: N wrapped, M failed' line the patch queue writes when a batch is in."""
    pat = re.compile(r"\[snitch\] " + re.escape(name) + r": (\d+) wrapped, (\d+) failed")
    end = time.time() + timeout
    while time.time() < end:
        hits = [pat.search(l) for l in log_lines(slot, 3000)]
        hits = [h for h in hits if h]
        if hits:
            return int(hits[-1].group(1)), int(hits[-1].group(2))
        time.sleep(2.0)
    return None


def garbage_case(slot, words, expect_cause, burn_marker):
    """A garbage run ends in a forced collection outside every section. A collection right after a natural one can be
    too short to be a stutter, and a natural collection of the same kind can land in the window, so the run is
    repeated until its own collection is the one logged and the overlay named it."""
    line, why, seen = None, "", ("", "", "")
    expect_overlay = "most garbage from SnitchTest"
    for attempt in range(4):
        t0 = time.time()
        code, out = s1(slot, "run", "--until", "armed|already going", "--max-wait", "8000", *words.split())
        if code != 0:
            check("console: " + words, False, out)
            return
        got = overlay_poll(slot, expect_overlay, timeout=5.0)
        if got[0] or not seen[0]:
            seen = got
        time.sleep(max(0.0, 3.0 - (time.time() - t0)))
        line, why = stutter_after_burn(slot, burn_marker, window=1.0)
        if line is not None and cause_of(line) == expect_cause and field(line, "mod") == "SnitchTest" and seen[0]:
            break
        time.sleep(2.0)
    check(words + ": the forced collection is logged as a stutter", line is not None,
          why if line is None else line[line.find(PREFIX):][:200])
    if line is None:
        return
    cause = cause_of(line)
    check(f"{words}: cause is {expect_cause}", cause == expect_cause, f"cause='{cause}'")
    mod = field(line, "mod")
    check(f"{words}: mod (most garbage) is SnitchTest", mod == "SnitchTest", line[line.find("cause:"):][:300])
    check(f"{words}: the line lists the garbage by mod", "garbage since the last collection" in line and "SnitchTest" in line,
          line[line.find("cause:"):][:300])
    overlay_check(words, expect_overlay, seen)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--slot", default=None)
    ap.add_argument("--deep", action="store_true",
                    help="only the deep-probe case; run it last, the deep probes stay in until the game restarts")
    a = ap.parse_args()
    slot = a.slot
    if a.deep:
        deep_part(slot)
        finish()

    # The overlay checks read the page through Sideload's MCP probe, which only a Debug Sideload has; without it
    # the bridge answers None to every eval.
    code, out = s1(slot, "eval", "snitch-overlay", "1 + 1")
    check("the overlay page answers eval (needs a Debug build of Sideload in the slot)",
          code == 0 and out.strip().splitlines()[-1:] == ["2"], out)

    code, out = s1(slot, "run", "--until", "(?i)sampl", "--max-wait", "8000", "snitch", "start")
    check("console: snitch start", code == 0, out)
    code, out = s1(slot, "run", "--until", r"\[overlay\] (compact|already compact)", "--max-wait", "8000", "snitch", "open")
    check("console: snitch open (compact overlay)", code == 0, out)

    for name in ("patch timing", "mod behaviour timing"):
        got = wait_wrapped(slot, name)
        check(f"{name}: wrappers installed by the queue", got is not None and got[0] > 0, f"{got}")
        # A method Harmony cannot rebuild is skipped: its wrapper is taken out again and the method stays as it was.
        if got is not None and got[1] > 0:
            lines = log_lines(slot, 6000)
            left = [l for l in lines if "could not time" in l and "left as it was" in l]
            stuck = [l for l in lines if "could not take the wrapper out" in l]
            check(f"{name}: every failed wrapper was taken out again", len(left) >= got[1] and not stuck,
                  f"failed={got[1]} left-as-it-was={len(left)} stuck={stuck[:2]}")
        else:
            check(f"{name}: no wrapper failed", got is not None, f"{got}")
    time.sleep(4.0)   # the stutter threshold needs 30 frames and one poll tick

    code, out = s1(slot, "eval", "snitch-overlay",
                   "(function () { var r = document.querySelector('.row.lead .name'); "
                   "var g = Array.prototype.map.call(document.querySelectorAll('.row.game .name'), function (e) { return e.textContent; }); "
                   "return (r ? r.textContent : '-') + '|' + g.join(';'); })()")
    names = out.strip().splitlines()[-1] if out.strip() else ""
    lead, _, games = names.partition("|")
    rows = [g for g in games.split(";") if g]
    check("overlay: the Game block has one row per kind of game work (2-3 in compact)",
          2 <= len(rows) <= 3 and all(g.startswith("Game ") and len(g) > 5 for g in rows), names)
    check("overlay: the Game block has no '(unattributed)' row", "unattributed" not in names, names)
    check("overlay: the leader is a mod, not the game", lead not in ("", "-") and not lead.startswith("Game"), names)

    case(slot, "snitch burn 80", "outside any section", "Scripts")
    time.sleep(1.0)
    case(slot, "snitch burn 80 section", "inside SnitchTest.Burn", "SnitchTest.Burn", "SnitchTest")
    time.sleep(1.0)
    case(slot, "snitch burn 80 patch", "inside SpikePatch.Prefix", "SnitchTest.SpikePatch.Prefix", "SnitchTest")
    time.sleep(1.0)
    case(slot, "snitch burn 80 behaviour", "inside SpikeBehaviour.Update", "SnitchTest.SpikeBehaviour.Update", "SnitchTest")
    time.sleep(1.0)
    case(slot, "snitch burn 80 coroutine", "inside coroutine BurnRoutine", "SnitchTest.SpikeKit.BurnRoutine (coroutine)", "SnitchTest")
    time.sleep(1.0)
    game_case(slot, deep=False)
    time.sleep(1.0)

    # 'snitch engine' lists the newest stutters (it keeps the last 64), so it is read right after the coroutine and
    # game burns; the earlier burns can be pushed out by natural stutters on a busy machine.
    out = "\n".join(command_lines(slot, "snitch engine 40", r"\[snitch\] stutters in the last \d+ s"))
    check("snitch engine lists the stutters with their cause",
          "cause SnitchTest.SpikeKit.BurnRoutine (coroutine)" in out and "cause Scripts" in out, out[-600:])

    # A collection right after a natural one has little to do and is no stutter, so it is retried until the
    # forced collection itself takes long enough to be one.
    m = None
    burns = []
    for attempt in range(5):
        s1(slot, "run", "--until", "armed", "--max-wait", "8000", "snitch", "burn", "gc")
        time.sleep(1.0)
        burns = [l for l in snitch_lines(slot) if "burn: forced a garbage collection" in l]
        m = re.search(r"collection \(([\d.]+) ms", burns[-1]) if burns else None
        if m and float(m.group(1)) >= 15.0:
            break
        time.sleep(3.0)
    check("burn gc: a forced collection took 15 ms or more", m is not None and float(m.group(1)) >= 15.0,
          burns[-1] if burns else "no burn line")
    overlay_shows(slot, "burn gc", "Garbage collection")
    line, why = stutter_after_burn(slot, "forced a garbage collection")
    check("burn gc: a stutter is logged", line is not None, why if line is None else line[line.find(PREFIX):][:200])
    if line:
        check("burn gc: cause is Garbage collection", cause_of(line) == "Garbage collection", line[:220])
        check("burn gc: frame is marked GC", " GC x" in line, line[:200])
    time.sleep(1.0)

    garbage_case(slot, "snitch burn garbage", "Garbage collection", "forced a game collection after the garbage run")
    code, out = s1(slot, "shot")
    shot_path = out.strip().splitlines()[-1] if out.strip() else ""
    check("screenshot after the garbage run", code == 0 and os.path.exists(shot_path), shot_path or out)
    time.sleep(1.0)
    garbage_case(slot, "snitch burn garbage net", ".NET garbage collection", "forced a .NET collection after the garbage run")

    out = "\n".join(command_lines(slot, "snitch garbage", r"\[snitch\] game heap: "))
    check("snitch garbage lists garbage by mod", "game heap" in out.lower(), out[-400:])
    finish()


def finish():
    failed = results.count(False)
    print(f"{len(results) - failed}/{len(results)} checks passed")
    sys.exit(1 if failed else 0)


def deep_part(slot):
    """The game-script burn with deep probes on. Separate run: the probes stay in for the rest of the session and
    change every frame's cost, so the other checks of a pass run before it."""
    code, out = s1(slot, "run", "--until", "(?i)sampl", "--max-wait", "8000", "snitch", "start")
    check("console: snitch start", code == 0, out)
    # Back to compact: an expanded overlay left by an earlier check stutters on its own and would be the newest
    # stutter the overlay line shows.
    code, out = s1(slot, "run", "--until", r"\[overlay\] (compact|already compact)", "--max-wait", "8000", "snitch", "open")
    check("console: snitch open (compact overlay)", code == 0, out)
    code, out = s1(slot, "run", "--until", "deep probes ON", "--max-wait", "8000", "snitch", "deep", "on")
    check("console: snitch deep on", code == 0 and "deep probes ON" in out, out[-300:])
    got = wait_deep(slot)
    check("deep probes: every queued game method went through the queue", got is not None and got[0] > 0, f"{got}")
    lines = log_lines(slot, 8000)
    wrapped = any("deep probes:" in l and "MonoBehaviourEvents.Update" in l and "/" in l for l in lines)
    refused = [l for l in lines if "could not wrap MonoBehaviourEvents.Update" in l]
    check("deep probes: MonoBehaviourEvents.Update is wrapped", wrapped and not refused,
          f"in a batch line={wrapped} refused={refused[:1]}")
    time.sleep(6.0)   # the frame median and the step medians settle on the deep probes' overhead
    game_case(slot, deep=True)


if __name__ == "__main__":
    main()
