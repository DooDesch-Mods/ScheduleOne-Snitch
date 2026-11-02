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


# ----- real mouse: OS input -> Unity Input -> EventSystem -> Sideload -> page -> host -----
#
# `s1 click` (Sideload ClickProbe) raises down, up and click in one frame on one object, and `sideloaddrag` holds
# the press but never raises a uGUI click. Neither sees a page rebuild between press and release, which is what
# swallowed real clicks. This section moves the Windows cursor and presses the left button with SendInput-level
# calls, held across several frames, so the click goes through the same path as a player's mouse.

def _win32():
    import ctypes
    from ctypes import wintypes
    u = ctypes.WinDLL("user32", use_last_error=True)
    k = ctypes.WinDLL("kernel32", use_last_error=True)
    try:
        u.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))  # per-monitor v2: physical pixels everywhere
    except (AttributeError, OSError) as e:
        print("note: DPI awareness not set: " + str(e))
    u.WindowFromPoint.argtypes = [wintypes.POINT]
    u.WindowFromPoint.restype = wintypes.HWND
    u.GetAncestor.restype = wintypes.HWND
    u.GetForegroundWindow.restype = wintypes.HWND
    return ctypes, wintypes, u, k


def game_window(root):
    """The largest visible top-level window of a process whose exe lies under the slot root, or None."""
    ctypes, wintypes, u, k = _win32()
    root = os.path.normcase(os.path.abspath(root))
    found = []

    @ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    def each(hwnd, _):
        if not u.IsWindowVisible(hwnd):
            return True
        pid = wintypes.DWORD()
        u.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        h = k.OpenProcess(0x1000, False, pid.value)
        if not h:
            return True
        buf = ctypes.create_unicode_buffer(1024)
        n = wintypes.DWORD(1024)
        ok = k.QueryFullProcessImageNameW(h, 0, buf, ctypes.byref(n))
        k.CloseHandle(h)
        if ok and os.path.normcase(buf.value).startswith(root + os.sep):
            rc = wintypes.RECT()
            u.GetClientRect(hwnd, ctypes.byref(rc))
            found.append((rc.right * rc.bottom, hwnd, rc.right, rc.bottom, pid.value))
        return True

    u.EnumWindows(each, 0)
    found.sort(reverse=True)
    return found[0][1:] if found and found[0][0] > 0 else None


def bring_front(u, hwnd, tries=4):
    """Brings the window forward and returns the foreground window. A tap of Alt lets this process take the
    foreground (Windows foreground lock); another application may take it back, so it retries."""
    fg = u.GetForegroundWindow()
    for _ in range(tries):
        if fg == hwnd:
            break
        u.keybd_event(0x12, 0, 0, 0)
        u.ShowWindow(hwnd, 9)
        u.SetForegroundWindow(hwnd)
        u.keybd_event(0x12, 0, 2, 0)
        time.sleep(0.5)
        fg = u.GetForegroundWindow()
    return fg


def real_click(root, ux, uy, sw, sh, hold_s=0.2):
    """Clicks at Unity screen point (ux, uy) (origin bottom left) of the game under root. Returns (ok, detail)."""
    ctypes, wintypes, u, k = _win32()
    win = game_window(root)
    if not win:
        return False, "no game window with an exe under " + root
    hwnd, cw, ch, pid = win
    fg = bring_front(u, hwnd)
    if fg != hwnd:
        return False, f"the game window is not in the foreground (fg={fg} game={hwnd} pid={pid})"
    pt = wintypes.POINT(int(round(ux * cw / sw)), int(round(ch - uy * ch / sh)))
    u.ClientToScreen(hwnd, ctypes.byref(pt))
    hit = u.GetAncestor(u.WindowFromPoint(pt), 2)
    if hit != hwnd:
        return False, f"point {pt.x},{pt.y} lies over another window ({hit}), not the game ({hwnd})"
    u.SetCursorPos(pt.x, pt.y)
    time.sleep(0.15)  # hover first, as a player does; uGUI raises pointer enter
    u.mouse_event(0x0002, 0, 0, 0, 0)  # left down
    time.sleep(hold_s)  # held across several frames and at least one state push interval
    u.mouse_event(0x0004, 0, 0, 0, 0)  # left up
    time.sleep(0.6)
    return True, f"screen {pt.x},{pt.y} client {cw}x{ch} unity {sw}x{sh} pid {pid}"


def focus_game(root):
    """Brings the game of this slot to the foreground. Returns (handles, error)."""
    ctypes, wintypes, u, k = _win32()
    win = game_window(root)
    if not win:
        return None, "no game window with an exe under " + root
    hwnd, cw, ch, pid = win
    if bring_front(u, hwnd) != hwnd:
        return None, f"the game window is not in the foreground (pid={pid})"
    return (ctypes, wintypes, u, hwnd, cw, ch), ""


def cursor_to(g, fx, fy):
    """Moves the Windows cursor to a fraction of the game's client area (y from the top)."""
    ctypes, wintypes, u, hwnd, cw, ch = g
    pt = wintypes.POINT(int(cw * fx), int(ch * fy))
    u.ClientToScreen(hwnd, ctypes.byref(pt))
    u.SetCursorPos(pt.x, pt.y)


def mouse_sweep(g, rounds=6, step=90):
    """Relative mouse motion in both axes, the raw input that turns the camera when look is on."""
    u = g[2]
    for _ in range(rounds):
        for dx, dy in ((step, 0), (0, step // 2), (-step, 0), (0, -step // 2)):
            for _ in range(5):
                u.mouse_event(0x0001, dx // 5, dy // 5, 0, 0)
                time.sleep(0.02)


def key_tap(g, vk):
    u = g[2]
    u.keybd_event(vk, 0, 0, 0)
    time.sleep(0.08)
    u.keybd_event(vk, 0, 2, 0)
    time.sleep(0.5)


def view(slot):
    """(position, yaw, pitch) of the player camera from the bridge's 'where'."""
    import json
    import math
    code, out = s1(slot, "where")
    try:
        d = json.loads(out[out.index("{"):out.rindex("}") + 1])
        f = d["camera"]["forward"]
        return tuple(d["position"]), math.degrees(math.atan2(f[0], f[2])), math.degrees(math.asin(max(-1.0, min(1.0, f[1]))))
    except (ValueError, KeyError, TypeError) as e:
        return None, 0.0, 0.0


def turned(a, b):
    """Degrees between two (yaw, pitch) pairs."""
    dy = abs((a[0] - b[0] + 180.0) % 360.0 - 180.0)
    return max(dy, abs(a[1] - b[1]))


def where(slot, ident):
    """(x, y, w, h, screenW, screenH) of a page element from 'snitch where', or None."""
    ok, out = run(slot, "snitch where " + ident, r"where " + ident + r" x=|no element|no size|not mounted|not expanded|unknown")
    m = re.search(r"where " + ident + r" x=(-?\d+) y=(-?\d+) w=(\d+) h=(\d+) screen=(\d+)x(\d+)", out)
    if not m or int(m.group(3)) <= 0 or int(m.group(4)) <= 0:
        return None
    return tuple(int(v) for v in m.groups())


def shot_image(slot):
    from PIL import Image
    code, out = s1(slot, "shot")
    path = out.strip().splitlines()[-1] if out.strip() else ""
    if code != 0 or not os.path.exists(path):
        return None, path or out
    return Image.open(path).convert("RGB"), path


def region(img, box, sw, sh):
    """Pixels of a Unity-screen box (centre x, y from the bottom, w, h) in a screenshot of any scale."""
    x, y, w, h = box
    sx, sy = img.width / sw, img.height / sh
    left, right = int((x - w / 2) * sx), int((x + w / 2) * sx)
    top, bottom = int((sh - y - h / 2) * sy), int((sh - y + h / 2) * sy)
    return list(img.crop((left, top, max(left + 1, right), max(top + 1, bottom))).getdata())


def input_section(slot, root):
    """The expanded overlay owns the mouse: the camera stays put, a click on the page does not reach the player,
    hover paints, Escape closes it, the status line and the phone QR show. Needs the overlay expanded."""
    g, err = focus_game(root)
    check("input: game window in the foreground", g is not None, err)
    if not g:
        return

    # Camera. Moving the mouse while the overlay is expanded must not turn the player camera.
    pos0, yaw0, pitch0 = view(slot)
    cursor_to(g, 0.5, 0.9)
    mouse_sweep(g)
    pos1, yaw1, pitch1 = view(slot)
    check("input: mouse movement over the expanded overlay does not turn the camera",
          pos0 is not None and turned((yaw0, pitch0), (yaw1, pitch1)) < 0.5,
          f"yaw {yaw0:.1f}->{yaw1:.1f} pitch {pitch0:.1f}->{pitch1:.1f}")

    # A click on empty desk, away from every window, must not reach the character (no step, no turn).
    cursor_to(g, 0.5, 0.97)
    time.sleep(0.2)
    ctypes, wintypes, u, hwnd, cw, ch = g
    u.mouse_event(0x0002, 0, 0, 0, 0)
    time.sleep(0.2)
    u.mouse_event(0x0004, 0, 0, 0, 0)
    time.sleep(0.5)
    pos2, yaw2, pitch2 = view(slot)
    check("input: a click on the empty overlay leaves position and camera as they were",
          pos0 is not None and pos2 == pos0 and turned((yaw0, pitch0), (yaw2, pitch2)) < 0.5, f"{pos0} -> {pos2}")
    code, log = s1(slot, "log", "-n", "300", "--grep", "overlay")
    check("input: the overlay still holds the cursor after the click",
          log.rfind("[overlay] cursor freed") > log.rfind("[overlay] cursor restored"), log_tail(log, "cursor"))

    # Hover: the Start/Stop button changes colour under the pointer. The pointer sits over the left padding of the
    # button, away from its text.
    run_box = where(slot, "run")
    if run_box:
        x, y, w, h, sw, sh = run_box
        cursor_to(g, 0.5, 0.97)
        time.sleep(0.4)
        img0, p0 = shot_image(slot)
        u.SetCursorPos(0, 0)
        ux, uy = x - w // 2 + 8, y
        cursor_to(g, 0.5, 0.97)
        pt = wintypes.POINT(int(round(ux * cw / sw)), int(round(ch - uy * ch / sh)))
        u.ClientToScreen(hwnd, ctypes.byref(pt))
        u.SetCursorPos(pt.x, pt.y)
        time.sleep(0.6)
        img1, p1 = shot_image(slot)
        if img0 is None or img1 is None:
            check("input: hover screenshots taken", False, str(p0) + " " + str(p1))
        else:
            a = region(img0, (ux, uy, 6, 6), sw, sh)
            b = region(img1, (ux, uy, 6, 6), sw, sh)
            ma = tuple(sum(c[i] for c in a) // len(a) for i in range(3))
            mb = tuple(sum(c[i] for c in b) // len(b) for i in range(3))
            check("input: hovering a button changes its background", sum(abs(p - q) for p, q in zip(ma, mb)) >= 30,
                  f"rgb {ma} -> {mb} at {ux},{uy} ({p0} / {p1})")
        cursor_to(g, 0.5, 0.97)
    else:
        check("input: 'snitch where run' finds the button for the hover check", False, "")

    # Phone: the button turns the pairing on and a QR appears; again turns it off.
    phone = where(slot, "phone")
    if phone:
        x, y, w, h, sw, sh = phone
        ok, detail = real_click(root, x, y, sw, sh)
        check("input: Phone clicked with the real mouse", ok, detail)
        time.sleep(1.5)
        code, log = s1(slot, "log", "-n", "400", "--grep", "snitch")
        check("input: Phone on started the phone remote", "phone remote on" in log, log_tail(log, "phone remote"))
        qr = where(slot, "qr")
        check("input: the QR is on screen after Phone on", qr is not None, str(qr))
        if qr:
            img, path = shot_image(slot)
            if img is not None:
                px = region(img, (qr[0], qr[1], qr[2], qr[3]), qr[4], qr[5])
                dark = sum(1 for c in px if sum(c) < 150)
                light = sum(1 for c in px if sum(c) > 650)
                check("input: the QR area shows a code (dark and light modules)", len(px) > 0 and dark > len(px) * 0.15
                      and light > len(px) * 0.15, f"dark={dark} light={light} of {len(px)} ({path})")
        phone = where(slot, "phone")
        if phone:
            real_click(root, phone[0], phone[1], phone[4], phone[5])
            time.sleep(1.5)
            code, log = s1(slot, "log", "-n", "400", "--grep", "snitch")
            check("input: Phone again turned the phone remote off", "phone remote off" in log, log_tail(log, "phone remote"))
            check("input: the QR is gone after Phone off", where(slot, "qr") is None, "")
    else:
        check("input: 'snitch where phone' finds the button", False, "")

    # Status line: a data server that cannot bind says so in one line.
    import socket
    holder = socket.socket()
    holder.bind(("127.0.0.1", 0))
    holder.listen(1)
    taken = holder.getsockname()[1]
    ok, out = run(slot, f"snitch servertest {taken}", r"data server (running|not running)")
    time.sleep(1.5)
    note = where(slot, "note")
    check("input: a taken data-server port shows the status line", "not running" in out and note is not None, out + " " + str(note))
    holder.close()
    ok, out = run(slot, "snitch servertest 6140", r"data server (running|not running)")
    time.sleep(1.5)
    check("input: the status line is gone once the data server runs", "running on 6140" in out and where(slot, "note") is None, out)

    # Escape closes the overlay in one press.
    key_tap(g, 0x1B)
    code, log = s1(slot, "log", "-n", "300", "--grep", "overlay")
    check("input: Escape closes the expanded overlay", "[overlay] hidden (escape)" in log, log_tail(log, "overlay"))
    check("input: Escape gave the cursor back", log.rfind("[overlay] cursor restored") > log.rfind("[overlay] cursor freed"), log_tail(log, "cursor"))
    img, path = shot_image(slot)
    print("note: screenshot after Escape (look for the pause menu): " + str(path))

    # Control: with the overlay closed the same mouse motion does turn the camera, so the check above could fail.
    pos3, yaw3, pitch3 = view(slot)
    mouse_sweep(g)
    pos4, yaw4, pitch4 = view(slot)
    check("input: control - the same mouse motion turns the camera once the overlay is closed",
          turned((yaw3, pitch3), (yaw4, pitch4)) > 5.0, f"yaw {yaw3:.1f}->{yaw4:.1f} pitch {pitch3:.1f}->{pitch4:.1f}")
    run(slot, "snitch open all", r"\[overlay\] (expanded|already expanded)")
    time.sleep(1.0)


PAINT_ONLY = {"transform", "background", "background-color", "background-image", "border-color", "border-radius",
              "box-shadow", "outline", "outline-width", "outline-color", "outline-style", "outline-offset"}


def inputstate(slot):
    """canLook, canMove, cursor lock and visibility read back from the game (DEBUG build)."""
    ok, out = run(slot, "snitch inputstate", r"inputstate freed=")
    m = re.search(r"inputstate freed=(\w+) canLook=(\w+) canMove=(\w+) cursorLock=(\w+) cursorVisible=(\w+)", out)
    return dict(zip(("freed", "look", "move", "lock", "visible"), m.groups())) if m else None


def decode_qr(slot, label):
    """Screenshot the QR area and decode it with OpenCV; (text, path) or (None, reason)."""
    try:
        import cv2
        import numpy as np
    except ImportError as e:
        return None, "opencv missing: " + str(e)
    qr = where(slot, "qr")
    if not qr:
        return None, "no qr element"
    img, path = shot_image(slot)
    if img is None:
        return None, path
    x, y, w, h, sw, sh = qr
    sx, sy = img.width / sw, img.height / sh
    pad = 12
    left, right = max(0, int((x - w / 2) * sx) - pad), min(img.width, int((x + w / 2) * sx) + pad)
    top, bottom = max(0, int((sh - y - h / 2) * sy) - pad), min(img.height, int((sh - y + h / 2) * sy) + pad)
    crop = img.crop((left, top, right, bottom))
    out = os.environ.get("QR_CROP")
    if out:
        crop.save(out)
    arr = cv2.cvtColor(np.array(crop), cv2.COLOR_RGB2BGR)
    for scale in (1, 2, 3):
        big = cv2.resize(arr, None, fx=scale, fy=scale, interpolation=cv2.INTER_NEAREST) if scale > 1 else arr
        text, _, _ = cv2.QRCodeDetector().detectAndDecode(big)
        if text:
            return text, path
    return None, "not decoded (" + str(path) + ")"


def hover_css_check():
    """Every :hover and :active rule in app.css sets only properties Sideload answers with a repaint."""
    css_path = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "Assets", "overlay", "app.css"))
    css = open(css_path, encoding="utf-8").read()
    rules = re.findall(r"([^{}]+):(?:hover|active)\s*\{([^}]*)\}", css)
    bad = []
    for sel, body in rules:
        for prop in re.findall(r"([\w-]+)\s*:", body):
            if prop.lower() not in PAINT_ONLY:
                bad.append(sel.strip() + " " + prop)
    sels = " ".join(s for s, _ in rules)
    need = [c for c in (".btn", ".segbtn", ".wclose", ".togbox", ".wbar", ".head") if c not in sels]
    check("hover: .btn .segbtn .wclose .togbox and the title bars have :hover rules", not need, "missing " + ",".join(need))
    check("hover: hover and active rules use paint-only properties", bool(rules) and not bad, f"{len(rules)} rules, bad={bad}")
    for state in ("hover", "active"):
        check(f"hover: .btn has an :{state} rule", re.search(r"\.btn:" + state + r"\s*\{", css) is not None, "")


def state_section(slot):
    """The expanded overlay owns the game's UI state, shows the phone QR and the status line, and Escape closes it.
    Driven by console commands and the in-process click probe; no OS input. Starts and ends expanded."""
    hover_css_check()

    ok, out = run(slot, "snitch open all", r"\[overlay\] (expanded|already expanded)")
    check("state: expanded", ok, out)
    time.sleep(1.5)
    code, log = s1(slot, "log", "-n", "400", "--grep", "overlay")
    check("state: UI state push logged with the expected properties on expand",
          "UI state pushed: mouse free, crosshair hidden, equipping disabled, inventory disabled, movement locked, "
          "look locked, blur disabled, HUD visible, compass visible" in log, log_tail(log, "UI state"))
    st = inputstate(slot)
    check("state: look and movement locked, cursor free while expanded",
          st is not None and st["freed"] == "True" and st["look"] == "False" and st["move"] == "False"
          and st["lock"] == "None" and st["visible"] == "True", str(st))

    # Compact gives the state back.
    ok, out = run(slot, "snitch open", r"\[overlay\] (compact|already compact)")
    time.sleep(0.5)
    st = inputstate(slot)
    code, log = s1(slot, "log", "-n", "200", "--grep", "overlay")
    check("state: compact pops the UI state and the player can look and move",
          st is not None and st["look"] == "True" and st["move"] == "True" and st["lock"] == "Locked"
          and log.rfind("UI state popped") > log.rfind("UI state pushed"), str(st))

    # Escape: closes to hidden, gives the state back, and the pause menu stays shut (the cursor stays locked).
    run(slot, "snitch open all", r"\[overlay\] (expanded|already expanded)")
    time.sleep(1.0)
    ok, out = run(slot, "snitch escape", r"escape: (sent|failed|no GameInput)")
    time.sleep(0.8)
    code, log = s1(slot, "log", "-n", "200", "--grep", "overlay")
    st = inputstate(slot)
    check("state: Escape closes the overlay to hidden", "[overlay] hidden (escape)" in log, log_tail(log, "overlay"))
    check("state: Escape does not open the pause menu (cursor locked, look and movement back)",
          st is not None and st["lock"] == "Locked" and st["look"] == "True" and st["move"] == "True", str(st))

    # Phone: on -> QR visible and decodes to the pairing URL; off -> QR gone.
    run(slot, "snitch open all", r"\[overlay\] (expanded|already expanded)")
    time.sleep(1.0)
    code, out = evaluate(slot, "document.getElementById('phone').className")
    if "on" in last(out).split():
        s1(slot, "click", APP, "#phone")
        time.sleep(2.0)
    code, out = s1(slot, "click", APP, "#phone")
    time.sleep(2.0)
    code, log = s1(slot, "log", "-n", "400", "--grep", "snitch")
    check("phone: Phone on starts the phone remote", "phone remote on" in log, log_tail(log, "phone remote"))
    qr = where(slot, "qr")
    check("phone: the QR element is on screen", qr is not None, str(qr))
    ok, out = run(slot, "snitch qrurl", r"qr url ")
    m = re.search(r"qr url (\S+)", out)
    expected = m.group(1) if m else ""
    check("phone: the host has a pairing URL", expected.startswith("http"), out)
    text, detail = decode_qr(slot, "phone")
    check("phone: the QR in the screenshot decodes to the pairing URL", text is not None and text == expected,
          f"decoded={text!r} expected={expected!r} {detail}")
    code, out = evaluate(slot, "document.getElementById('pairu').textContent")
    check("phone: the pairing line names the path in use", last(out) != "", out)
    code, out = s1(slot, "click", APP, "#phone")
    time.sleep(2.0)
    code, log = s1(slot, "log", "-n", "400", "--grep", "snitch")
    check("phone: Phone again turns the remote off", "phone remote off" in log, log_tail(log, "phone remote"))
    check("phone: the QR is gone after Phone off", where(slot, "qr") is None, "")

    # Status line: a data server that cannot bind says so in one line.
    import socket
    code, log = s1(slot, "log", "-n", "6000", "--grep", "data server on")
    pm = re.findall(r"data server on http://127\.0\.0\.1:(\d+)/", log)
    port0 = int(pm[-1]) if pm else 6140
    holder = socket.socket()
    holder.bind(("127.0.0.1", 0))
    holder.listen(1)
    taken = holder.getsockname()[1]
    ok, out = run(slot, f"snitch servertest {taken}", r"data server (running|not running)")
    time.sleep(2.5)
    code, text = evaluate(slot, "document.getElementById('note').textContent")
    check("status: a taken data-server port shows one line", "not running" in out
          and last(text) == f"Port {taken} is in use, the dashboard is off." and where(slot, "note") is not None, out + " " + text)
    holder.close()
    ok, out = run(slot, f"snitch servertest {port0}", r"data server (running|not running)")
    time.sleep(2.5)
    check("status: the line is gone once the data server runs", f"running on {port0}" in out and where(slot, "note") is None, out)


def slot_root(slot):
    sys.path.insert(0, os.path.dirname(S1))
    import s1 as bridge
    name = slot or bridge.my_slot()
    return name, (bridge.SLOTS[name][1] if name in bridge.SLOTS else None)


def real_mouse_section(slot):
    name, root = slot_root(slot)
    if not root:
        check("real mouse: slot root known", False, f"slot {name!r}")
        return
    for n in (1, 2):  # twice: Start then Stop, so sampling ends where it began
        ok, out = run(slot, "snitch where run", r"where run x=|no element|no size|not mounted|not expanded|unknown")
        m = re.search(r"where run x=(-?\d+) y=(-?\d+) w=(\d+) h=(\d+) screen=(\d+)x(\d+)", out)
        if m and (int(m.group(3)) <= 0 or int(m.group(4)) <= 0):
            m = None
        check(f"real mouse {n}: 'snitch where run' finds the Start/Stop button on screen (DEBUG build)", m is not None, out)
        if not m:
            return
        x, y, w, h, sw, sh = (int(v) for v in m.groups())
        code, before = s1(slot, "log", "-n", "300", "--grep", "overlay")
        ok, detail = real_click(root, x, y, sw, sh)
        check(f"real mouse {n}: cursor pressed and released over the game window", ok, detail)
        if not ok:
            return
        code, after = s1(slot, "log", "-n", "300", "--grep", "overlay")
        seen = set(before.splitlines())
        new = [l for l in after.splitlines() if l not in seen]
        hits = [l for l in new if "[overlay] sampling" in l]
        held = [l for l in new if "state push held" in l]
        check(f"real mouse {n}: the click on Start/Stop reached the host", bool(hits),
              (hits[-1] if hits else "new overlay lines: " + " | ".join(new[-4:])) + (" (push held)" if held else ""))
    input_section(slot, root)


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
    ap.add_argument("--real-mouse", action="store_true",
                    help="opt in to the sections that move the Windows cursor and press keys; never in a default run")
    ap.add_argument("--mouse-only", action="store_true",
                    help="only the real-mouse section (needs --real-mouse); works without eval")
    a = ap.parse_args()
    if a.mouse_only and not a.real_mouse:
        print("FAIL --mouse-only moves the real cursor; add --real-mouse to confirm.")
        return 2
    slot = a.slot
    open_max_ms = a.open_max_ms

    if a.mouse_only:
        ok, out = run(slot, "snitch open all", r"\[overlay\]")
        check("snitch open all -> expanded", ok, out)
        time.sleep(1.0)
        real_mouse_section(slot)
        ok, out = run(slot, "snitch close", r"\[overlay\] (hidden|already hidden)")
        check("snitch close -> hidden", ok, out)
        failed = results.count(False)
        print(f"{len(results) - failed}/{len(results)} PASS")
        return 1 if failed else 0

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

    state_section(slot)
    if a.real_mouse:
        real_mouse_section(slot)

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
