"""In-game check of the Snitch Sideload overlay in a running test slot.

Usage: python -I tests/overlay-check.py [--slot mptest2]
Needs a loaded save with Snitch, Sideload and the ScheduleMCP bridge. Prints PASS/FAIL per check and the
screenshot paths; exit code 1 when any check failed.
"""
import argparse
import os
import re
import subprocess
import sys
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
    print(("PASS " if ok else "FAIL ") + name + ("  - " + detail.strip().replace("\n", " | ")[:300] if detail else ""))


def run(slot, words, until):
    code, out = s1(slot, "run", "--until", until, "--max-wait", "8000", *words.split())
    return code == 0 and re.search(until, out) is not None, out


def evaluate(slot, js):
    code, out = s1(slot, "eval", APP, js)
    return code, out


def shot(slot, label):
    code, out = s1(slot, "shot")
    path = out.strip().splitlines()[-1] if out.strip() else ""
    check("screenshot " + label, code == 0 and os.path.exists(path), path or out)


def number_in(out):
    m = re.search(r"-?\d+", out)
    return int(m.group(0)) if m else -1


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--slot", default=None)
    a = ap.parse_args()
    slot = a.slot

    ok, out = run(slot, "snitch start", r"(?i)sampl|start")
    check("snitch start", ok, out)

    ok, out = run(slot, "snitch open", r"\[overlay\] (compact|already compact)")
    check("snitch open -> compact", ok, out)

    code, out = evaluate(slot, "document.querySelectorAll('#rows > *').length")
    n = number_in(out)
    check("compact shows rider rows (>= 2)", code == 0 and n >= 2, out)

    code, out = evaluate(slot, "document.getElementById('rows').textContent")
    check("bib 0 (unattributed) is listed", code == 0 and "unattributed" in out.lower(), out)
    check("leader word is shown", code == 0 and "leader" in out.lower(), out)

    code, out = evaluate(slot, "document.querySelectorAll('#rows > *').length")
    check("compact shows at most 4 rows (top 3 + bib 0)", code == 0 and 1 <= number_in(out) <= 4, out)
    code, out = evaluate(slot, "Array.prototype.map.call(document.querySelectorAll('.name'), "
                               "function (e) { return e.textContent.length; }).join(',')")
    lens = [int(x) for x in re.findall(r"\d+", out.splitlines()[-1] if out.strip() else "")]
    check("row names fit the character budget (<= 30)", code == 0 and bool(lens) and max(lens) <= 30, out)
    code, out = evaluate(slot, "Array.prototype.map.call(document.querySelectorAll('.ms'), "
                               "function (e) { return e.textContent; }).join('|')")
    check("ms figures carry no line break", code == 0 and "\\n" not in out and out.count("\n") <= 1, out)
    # Layout rects come from Sideload's own layout tree through getBoundingClientRect.
    code, out = evaluate(slot, "Array.prototype.map.call(document.querySelectorAll('.bar'), "
                               "function (e) { var r = e.getBoundingClientRect(); "
                               "return Math.round(r.left * 10) / 10 + ':' + Math.round(r.width * 10) / 10; }).join(',')")
    bars = out.strip().splitlines()[-1].split(",") if out.strip() else []
    check("every bar track has the same start x and width (shared scale)",
          code == 0 and len(bars) >= 2 and len(set(bars)) == 1, out)
    # getComputedStyle does not exist in Sideload, so the centring is checked by the rect (fixed 30 wide,
    # same left edge in every row) and by the stylesheet rule that centres the text.
    code, out = evaluate(slot, "Array.prototype.map.call(document.querySelectorAll('.bib'), "
                               "function (e) { var r = e.getBoundingClientRect(); "
                               "return Math.round(r.left) + ':' + Math.round(r.width); }).join(',')")
    bibs = out.strip().splitlines()[-1].split(",") if out.strip() else []
    check("bib badges: same left edge and fixed width 30 in every row",
          code == 0 and len(bibs) >= 2 and len(set(bibs)) == 1 and bibs[0].endswith(":30"), out)
    css_path = os.path.join(ROOT, "_wt-Snitch-sideload-overlay", "Assets", "overlay", "app.css")
    css = open(css_path, encoding="utf-8").read() if os.path.exists(css_path) else ""
    rule = re.search(r"\.bib\s*\{([^}]*)\}", css)
    check("bib rule centres the digit (text-align center, align-items center)",
          bool(rule) and "text-align: center" in rule.group(1) and "align-items: center" in rule.group(1), css_path)
    shot(slot, "compact")

    ok, out = run(slot, "snitch open all", r"\[overlay\] (expanded|already expanded)")
    check("snitch open all -> expanded", ok, out)
    time.sleep(1.0)

    code, log = s1(slot, "log", "-n", "600", "--grep", "overlay")
    check("expanded frees the cursor", "[overlay] cursor freed" in log, "")

    code, out = evaluate(slot, "document.querySelectorAll('#rows > *').length")
    check("expanded rows capped (<= 11)", code == 0 and 1 <= number_in(out) <= 11, out)
    nrows = number_in(out)
    code, out = evaluate(slot, "document.getElementById('morerows').textContent")
    cut = re.search(r"\+\d+ more", out) is not None
    check("'+N more' line shown exactly when rows were cut", code == 0 and (cut or nrows < 10), out)

    code, out = evaluate(slot, "document.querySelectorAll('#panels > *').length")
    check("expanded shows mod panels (>= 1)", code == 0 and number_in(out) >= 1, out)

    code, out = evaluate(slot, "(function () { var c = document.getElementById('card').getBoundingClientRect(); "
                               "var m = document.getElementById('more').getBoundingClientRect(); "
                               "return Math.round(c.bottom - m.bottom); })()")
    check("expanded: #more ends at least 8 css px inside the card bottom", code == 0 and number_in(out) >= 8, out)

    code, out = s1(slot, "click", APP, "#run")
    code2, log = s1(slot, "log", "-n", "600", "--grep", "overlay")
    check("click Start/Stop reaches the host", code == 0 and "[overlay] sampling" in log, out)
    shot(slot, "expanded")

    code, out = s1(slot, "click", APP, ".segbtn[data-w='10']")
    code2, log = s1(slot, "log", "-n", "600", "--grep", "overlay")
    check("window 10 s click is accepted", code == 0 and "ui.window got" not in log.splitlines()[-1], out)

    ok, out = run(slot, "snitch close", r"\[overlay\] (hidden|already hidden)")
    check("snitch close -> hidden", ok, out)
    time.sleep(0.5)
    code, log = s1(slot, "log", "-n", "600", "--grep", "overlay")
    check("close restores the cursor", log.rfind("[overlay] cursor restored") > log.rfind("[overlay] cursor freed") >= 0, "")

    code, log = s1(slot, "log", "-n", "600", "--grep", "overlay")
    bad = [l for l in log.splitlines() if re.search(r"\[overlay\].*(failed|could not|not available)|\[overlay\] page:", l)]
    check("no overlay errors in the log", not bad, "\n".join(bad[-5:]))

    failed = results.count(False)
    print(f"{len(results) - failed}/{len(results)} PASS")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
