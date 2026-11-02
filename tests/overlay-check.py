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
    args = words if isinstance(words, list) else words.split()
    code, out = s1(slot, "run", "--until", until, "--max-wait", "8000", *args)
    return code == 0 and re.search(until, out) is not None, out


def last(out):
    """The value line of an eval answer, without quotes."""
    lines = [l for l in out.strip().splitlines() if l.strip()]
    return lines[-1].strip().strip('"') if lines else ""


def rect(slot, sel):
    """left,top,width,height of the first match as ints, or None when there is none or it has no size."""
    js = ("(function (s) { var e = document.querySelector(s); if (!e) return 'none'; var r = e.getBoundingClientRect(); "
          "return [r.left, r.top, r.width, r.height].map(Math.round).join(','); })(" + repr(sel) + ")")
    code, out = evaluate(slot, js)
    v = last(out)
    if code != 0 or not re.fullmatch(r"-?\d+,-?\d+,-?\d+,-?\d+", v):
        return None
    r = tuple(int(x) for x in v.split(","))
    return r if r[2] > 0 and r[3] > 0 else None


def drag(slot, a, b, steps, sel):
    return run(slot, ["--", "sideloaddrag", APP, a, b, str(steps), sel], r"drag on .* released")


def viewport(slot):
    code, out = evaluate(slot, "innerWidth + ',' + innerHeight")
    v = last(out)
    return tuple(int(x) for x in v.split(",")) if re.fullmatch(r"\d+,\d+", v) else (1920, 1080)


def all_on_screen(slot):
    vw, vh = viewport(slot)
    code, out = evaluate(slot, "Array.prototype.map.call(document.querySelectorAll('.win'), function (e) { "
                               "var r = e.getBoundingClientRect(); return [r.left, r.top, r.right, r.bottom]"
                               ".map(Math.round).join(','); }).join(';')")
    boxes = [tuple(int(x) for x in b.split(",")) for b in last(out).split(";") if re.fullmatch(r"-?\d+(,-?\d+){3}", b)]
    bad = [b for b in boxes if b[0] < 0 or b[1] < 0 or b[2] > vw or b[3] > vh]
    return bool(boxes) and not bad, f"viewport={vw}x{vh} boxes={boxes} outside={bad}"


def overlap(a, b):
    return a[0] < b[0] + b[2] and b[0] < a[0] + a[2] and a[1] < b[1] + b[3] and b[1] < a[1] + a[3]


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


def count_saved(slot):
    code, log = s1(slot, "log", "-n", "4000", "--grep", "layout saved")
    return len([l for l in log.splitlines() if "[overlay] layout saved" in l])


def log_tail(log, word):
    lines = [l for l in log.splitlines() if word in l]
    return lines[-1] if lines else "no line with '" + word + "'"


def windows_section(slot):
    """Expanded mode is running. Starts from the default layout and leaves the default layout behind."""
    ok, out = run(slot, "snitch layout reset", r"layout reset")
    time.sleep(1.0)

    code, out = evaluate(slot, "Array.prototype.map.call(document.querySelectorAll('.win'), function (e) { "
                               "return e.getAttribute('data-win'); }).join(',')")
    ids = [i for i in last(out).split(",") if i]
    mods = [i for i in ids if i not in ("standings", "states")]
    check("expanded has the Standings window, the States window and a mod window",
          "standings" in ids and "states" in ids and len(mods) >= 1, f"windows={ids}")
    check("a 'Windows' chip exists for every window besides Standings",
          number_in(evaluate(slot, "document.querySelectorAll('#chips > *').length")[1]) >= len(ids) - 1, f"windows={ids}")

    # Default layout: every window open, none on top of another, all inside the screen.
    code, out = evaluate(slot, "Array.prototype.map.call(document.querySelectorAll('.win'), function (e) { "
                               "var r = e.getBoundingClientRect(); return [r.left, r.top, r.width, r.height]"
                               ".map(Math.round).join(','); }).join(';')")
    boxes = [tuple(int(x) for x in b.split(",")) for b in last(out).split(";") if re.fullmatch(r"-?\d+(,-?\d+){3}", b)]
    clash = [(i, j) for i in range(len(boxes)) for j in range(i + 1, len(boxes)) if overlap(boxes[i], boxes[j])]
    check("default layout: no two windows overlap", len(boxes) == len(ids) and not clash, f"boxes={boxes} clash={clash}")
    ok, detail = all_on_screen(slot)
    check("default layout: every window is on screen", ok, detail)
    shot(slot, "expanded, default layout")

    # Which mod window to work with: the right-most one, so the clamp test below has room to run into the edge.
    code, out = evaluate(slot, "(function () { var best = null, bx = -1; Array.prototype.forEach.call("
                               "document.querySelectorAll('.mwin'), function (e) { var l = e.getBoundingClientRect().left; "
                               "if (l > bx) { bx = l; best = e; } }); return best ? best.getAttribute('data-win') + '|' + best.id : ''; })()")
    pick = last(out).split("|")
    if len(pick) != 2:
        check("a mod window can be picked for the drag checks", False, out)
        return
    wid, eid = pick
    n = eid.split("-")[1]
    sel = f"[data-win='{wid}']"
    base = rect(slot, sel)
    check("mod window has a size", base is not None, f"{wid} {base}")
    if not base:
        return

    ok, out = drag(slot, "0.2,0.5", "0.5,0.5", 8, f"#bar-{n}")
    moved = rect(slot, sel)
    check("title-bar drag moves a window", ok and moved is not None and moved[0] - base[0] > 40,
          f"before={base} after={moved}")
    check("the drag keeps the window size", moved is not None and moved[2:] == base[2:], f"before={base} after={moved}")
    code, log = s1(slot, "log", "-n", "800", "--grep", "overlay")
    check("drag end writes the layout ('layout saved')", re.search(r"\[overlay\] layout saved \(\d+ windows\)", log) is not None, "")

    ok, out = drag(slot, "0.5,0.5", "8,8", 10, f"#grip-{n}")
    grown = rect(slot, sel)
    check("grip drag changes the size", ok and grown is not None and grown[2] > moved[2] + 40 and grown[3] > moved[3] + 40,
          f"before={moved} after={grown}")
    code, out = evaluate(slot, "document.getElementById('ghost').style.display + '|' + "
                               "document.querySelector(" + repr(sel) + ").style.transform")
    check("after a resize the outline is gone and the window carries no drag transform",
          last(out).split("|")[0] == "none" and last(out).split("|")[-1] in ("", "none"), out)

    shot(slot, "expanded, one window moved and enlarged")

    ok, out = drag(slot, "0.5,0.5", "-12,-12", 10, f"#grip-{n}")
    small = rect(slot, sel)
    check("grip drag stops at the minimum size (>= 240 x 120)", ok and small is not None and small[2] >= 240 and small[3] >= 120
          and small[2] < grown[2], f"before={grown} after={small}")

    # Push the window against the right edge: it must stay on screen.
    ok, out = drag(slot, "0.2,0.5", "0.95,0.5", 8, f"#bar-{n}")
    ok2, detail = all_on_screen(slot)
    far = rect(slot, sel)
    check("a window dragged to the right edge stays on screen", ok and ok2, f"{detail} window={far}")

    # A click brings the window to the front: it is the last .win in z-order, which the page reports as zIndex.
    code, out = s1(slot, "click", APP, "#card .brand")
    code2, out2 = evaluate(slot, "(function () { var a = document.getElementById('card').style.zIndex; "
                                 "var b = document.querySelector(" + repr(sel) + ").style.zIndex; return a + '|' + b; })()")
    zs = last(out2).split("|")
    front_ok = len(zs) == 2 and zs[0].isdigit() and (not zs[1].isdigit() or int(zs[0]) > int(zs[1]))
    check("a click on a window brings it to the front", code == 0 and front_ok, out2)

    # Persistence: the layout survives hiding the overlay and opening it again.
    before = rect(slot, sel)
    run(slot, "snitch close", r"\[overlay\] (hidden|already hidden)")
    time.sleep(0.5)
    ok, out = run(slot, "snitch open all", r"\[overlay\] (expanded|already expanded)")
    time.sleep(1.5)
    after = rect(slot, sel)
    check("layout survives 'snitch close' + 'snitch open all'", before is not None and after == before,
          f"before={before} after={after}")

    # Close and reopen through the Windows strip.
    code, out = evaluate(slot, "document.querySelector(" + repr(sel) + ").id")
    eid = last(out)
    n = eid.split("-")[1] if "-" in eid else n
    code, out = s1(slot, "click", APP, f"#close-{n}")
    time.sleep(0.5)
    gone = rect(slot, sel)
    check("close button removes the window", code == 0 and gone is None, f"{gone} {out}")
    code, out = s1(slot, "click", APP, f"#chip-{n}")
    time.sleep(0.5)
    back = rect(slot, sel)
    check("the Windows strip reopens it", code == 0 and back is not None, f"{back} {out}")

    # A closed window stays closed across hide and show.
    s1(slot, "click", APP, f"#close-{n}")
    time.sleep(0.5)
    run(slot, "snitch close", r"\[overlay\] (hidden|already hidden)")
    time.sleep(0.5)
    run(slot, "snitch open all", r"\[overlay\] (expanded|already expanded)")
    time.sleep(1.5)
    check("a closed window stays closed after close + open all", rect(slot, sel) is None, "")

    ok, out = run(slot, "snitch layout reset", r"layout reset")
    time.sleep(1.0)
    reset = rect(slot, sel)
    check("'snitch layout reset' restores the default layout", ok and reset == base, f"default={base} now={reset}")
    ok, detail = all_on_screen(slot)
    check("after the reset every window is on screen", ok, detail)
    shot(slot, "expanded, after reset")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--slot", default=None)
    ap.add_argument("--open-max-ms", type=float, default=50.0)
    a = ap.parse_args()
    slot = a.slot
    open_max_ms = a.open_max_ms

    ok, out = run(slot, "snitch start", r"(?i)sampl|start")
    check("snitch start", ok, out)

    ok, out = run(slot, "snitch layout reset", r"layout reset")
    check("snitch layout reset (clean start)", ok, out)

    ok, out = run(slot, "snitch help", r"layout reset")
    check("snitch help lists 'layout reset'", ok, out)

    code, log = s1(slot, "log", "-n", "20000", "--grep", "background")
    check("the page was built in the background after the save loaded",
          "[overlay] page built in the background" in log, log_tail(log, "background"))

    ok, out = run(slot, "snitch open", r"\[overlay\] (compact|already compact)")
    check("snitch open -> compact", ok, out)
    check("opening switches the built page on instead of mounting it", "mounting it now" not in out, out)
    time.sleep(1.0)

    # The host logs the worst frame of the switch-on (the frames before the first state push is laid out), the
    # hitch the key itself causes. 50 ms is three frames at 60 fps; the old open mounted and built the page in that
    # frame (first build logged at about 500 ms).
    code, log = s1(slot, "log", "-n", "400", "--grep", "open cost")
    costs = [float(x) for x in re.findall(r"open cost: switch-on (\d+(?:\.\d+)?) ms", log)]
    check(f"open cost: switch-on frames <= {open_max_ms:.0f} ms", bool(costs) and costs[-1] <= open_max_ms,
          log_tail(log, "open cost"))

    # Compact keeps the cursor locked, so nothing in it may look clickable: every button and grip is hidden.
    code, out = evaluate(slot, "(function () { var bad = []; var els = document.querySelectorAll('button, .grip'); "
                               "for (var i = 0; i < els.length; i++) { var e = els[i], hid = false; "
                               "for (var p = e; p && p.style; p = p.parentNode) { if (p.style.display === 'none') { hid = true; break; } } "
                               "if (!hid) { var r = e.getBoundingClientRect(); if (r.width > 0 && r.height > 0) bad.push(e.id || e.className); } } "
                               "return els.length + '|' + bad.join(','); })()")
    parts = last(out).split("|")
    check("compact shows no button or grip", code == 0 and len(parts) == 2 and parts[0].isdigit() and int(parts[0]) > 0
          and parts[1] == "", out)
    code, out = evaluate(slot, "(function () { var e = document.getElementById('chint'); "
                               "var r = e.getBoundingClientRect(); return (r.height > 0 ? 'shown' : 'hidden') + '|' + e.textContent; })()")
    hint = last(out)
    check("compact shows the one-line hint (key to open, hold Alt to move)",
          hint.startswith("shown|") and "to open" in hint and "Alt" in hint, out)
    js_path = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "Assets", "overlay", "app.js"))
    js_src = open(js_path, encoding="utf-8").read() if os.path.exists(js_path) else ""
    check("compact empty text points to the key, 'Press Start' only in expanded (app.js)",
          "'Sampling stopped - ' + st.key + ' to open'" in js_src
          and re.search(r"st\.mode === 'expanded' \? 'Sampling is stopped\. Press Start\.'", js_src) is not None, js_path)

    vw, vh = viewport(slot)
    card0 = rect(slot, "#card")
    check("compact card is on screen at its default place", card0 is not None and card0[0] >= 0 and card0[1] >= 0
          and card0[0] + card0[2] <= vw and card0[1] + card0[3] <= vh, f"card={card0} viewport={vw}x{vh}")
    # The compact card is dragged by its header; the host leaves the raycaster off in compact, so this also shows
    # that the drag path works without it.
    if card0:
        saved0 = count_saved(slot)
        ok, out = drag(slot, "0.3,0.5", "0.5,0.5", 30, "#head")
        card1 = rect(slot, "#card")
        check("compact card moves when its header is dragged", ok and card1 is not None and card1[0] - card0[0] > 40,
              f"before={card0} after={card1}")
        saved = count_saved(slot) - saved0
        check("a 30-step compact drag writes the layout exactly once, on release", saved == 1, f"layout saved lines={saved}")
        ok, out = run(slot, "snitch layout reset", r"layout reset")
        time.sleep(0.5)
        card2 = rect(slot, "#card")
        check("layout reset puts the compact card back", ok and card2 == card0, f"default={card0} now={card2}")

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
    code, out = evaluate(slot, "Array.prototype.map.call(document.querySelectorAll('.bibi'), "
                               "function (e) { return e.textContent; }).join(',')")
    compact_bibs = out.strip().splitlines()[-1].split(",") if out.strip() else []
    compact_named = len([b for b in compact_bibs if b.strip().isdigit() and b.strip() != "0"])
    check("compact lists bib 0", code == 0 and "0" in [b.strip() for b in compact_bibs], out)

    # Scale: the ladder step of the web dashboard. The leader's bar width is ms / scale, so the exposed scale must be
    # the first ladder step >= the leader's ms (a value within 2 % of a step may land on either side).
    ladder = [0.5, 1, 2, 3, 4, 5, 6, 8, 10, 12, 16, 20, 25, 33, 50, 100, 250, 500, 1000]
    code, out = evaluate(slot, "document.getElementById('rows').getAttribute('data-scale')")
    scale = float(re.search(r"\d+(\.\d+)?", out).group(0)) if re.search(r"\d+(\.\d+)?", out) else -1
    code2, out2 = evaluate(slot, "(function () { var r = document.querySelector('.row.lead .ms'); "
                                 "return r ? r.textContent : ''; })()")
    m = re.search(r"\d+(\.\d+)?", out2)
    lead_ms = float(m.group(0)) if m else -1
    expected = {next((x for x in ladder if x >= v), float(-(-v // 1))) for v in (lead_ms * 0.98, lead_ms * 1.02)} if lead_ms > 0 else set()
    check("bar scale is a step of the dashboard ladder and fits the leader",
          scale in ladder and scale in expected, f"scale={scale} leader_ms={lead_ms} expected={sorted(expected)}")

    check("segbtn.on is white on navy, not yellow",
          bool(re.search(r"\.segbtn\.on\s*\{[^}]*background-color:\s*#FFFFFF", css, re.I))
          and not re.search(r"\.segbtn[^{]*\{[^}]*F2C200", css, re.I), css_path)
    check("leader chip is the only other yellow fill (.gt.chip, .mark)",
          len(re.findall(r"F2C200", css, re.I)) == 2, "")
    check("no fixed ink colour on the bib rules (set per team colour by script)",
          not re.search(r"\.(bib|bibi|pbib)\s*\{[^}]*[;{ ]color:", css, re.I), "")
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

    code, out = evaluate(slot, "Array.prototype.map.call(document.querySelectorAll('.bibi'), "
                               "function (e) { return e.textContent; }).join(',')")
    exp_bibs = out.strip().splitlines()[-1].split(",") if out.strip() else []
    exp_named = len([b for b in exp_bibs if b.strip().isdigit() and b.strip() != "0"])
    code, out = evaluate(slot, "document.getElementById('morerows').textContent")
    more = re.search(r"\+(\d+) more", out)
    total_named = exp_named + (int(more.group(1)) if more else 0)
    check("compact shows 3 named riders plus bib 0 when >= 3 mods report",
          total_named >= 3 and compact_named == 3, f"compact_named={compact_named} total_named={total_named}")
    check("expanded shows at most 10 named riders plus bib 0", exp_named <= 10 and "0" in [b.strip() for b in exp_bibs],
          f"expanded_named={exp_named}")

    code, out = evaluate(slot, "document.querySelectorAll('.mwin').length")
    check("expanded shows mod windows (>= 1)", code == 0 and number_in(out) >= 1, out)

    code, out = evaluate(slot, "Array.prototype.map.call(document.querySelectorAll('.togbox'), "
                               "function (e) { return e.textContent; }).join(',')")
    tog = out.strip().splitlines()[-1].split(",") if out.strip() else []
    check("every toggle carries On/Off text", code == 0 and bool(tog) and all(t.strip() in ("On", "Off") for t in tog), out)

    code, out = evaluate(slot, "document.getElementById('phone').textContent")
    check("phone button reads 'Phone' or 'Phone on'", code == 0 and re.search(r"Phone( on)?\s*$", out.strip()) is not None, out)

    code, log = s1(slot, "log", "-n", "2000", "--grep", "font")
    own = "\n".join(l for l in log.splitlines() if "snitch-overlay" in l)
    loaded = len(re.findall(r"@font-face .*loaded from", own))
    check("all four Barlow faces loaded (BundleFonts log)", loaded >= 4 and "has no @font-face that loaded" not in own
          and "is not loaded" not in own, f"loaded lines={loaded} " + own[-200:])
    check("brand rule names Barlow Condensed", bool(re.search(r"\.brand\s*\{[^}]*Barlow Condensed", css)), "")

    code, out = s1(slot, "click", APP, "#run")
    code2, log = s1(slot, "log", "-n", "600", "--grep", "overlay")
    check("click Start/Stop reaches the host", code == 0 and "[overlay] sampling" in log, out)
    shot(slot, "expanded")

    code, out = s1(slot, "click", APP, ".segbtn[data-w='10']")
    code2, log = s1(slot, "log", "-n", "600", "--grep", "overlay")
    check("window 10 s click is accepted", code == 0 and "ui.window got" not in log.splitlines()[-1], out)

    windows_section(slot)

    ok, out = run(slot, "snitch close", r"\[overlay\] (hidden|already hidden)")
    check("snitch close -> hidden", ok, out)
    time.sleep(0.5)
    code, log = s1(slot, "log", "-n", "600", "--grep", "overlay")
    check("close restores the cursor", log.rfind("[overlay] cursor restored") > log.rfind("[overlay] cursor freed") >= 0, "")

    code, log = s1(slot, "log", "-n", "600", "--grep", "overlay")
    bad = [l for l in log.splitlines() if re.search(r"\[overlay\].*(failed|could not|not available)|\[overlay\] page:", l)]
    check("no overlay errors in the log", not bad, "\n".join(bad[-5:]))
    code, log = s1(slot, "log", "-n", "3000", "--grep", "snitch-overlay")
    sbad = [l for l in log.splitlines() if re.search(r"(?i)\b(error|exception|failed)\b", l) and "snitch-overlay" in l]
    check("no Sideload errors for snitch-overlay in the log", not sbad, "\n".join(sbad[-5:]))
    code, log = s1(slot, "log", "-n", "3000", "--grep", "overlay")
    dropped = [l for l in log.splitlines() if "layout" in l and re.search(r"(?i)dropped|ignored|invalid|malformed", l)]
    check("no layout entry was dropped as malformed", not dropped, "\n".join(dropped[-5:]))

    failed = results.count(False)
    print(f"{len(results) - failed}/{len(results)} PASS")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
