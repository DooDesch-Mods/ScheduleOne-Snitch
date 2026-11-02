"""Regression check of the Snitch surfaces the overlay move left unchanged, in a running test slot.

Usage: python -I tests/regression-check.py [--slot mptest2]
Needs a loaded save with Snitch and the ScheduleMCP bridge. Covers the console commands, the data server on
127.0.0.1:<ServerPort> and the log of the session. Prints PASS/FAIL per check; exit code 1 when any check failed.
"""
import argparse
import json
import os
import re
import subprocess
import sys
import urllib.error
import urllib.request

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
S1 = os.path.join(ROOT, "Workspace", "tools", "bridge", "s1.py")

results = []


def s1(slot, *args, timeout=60):
    cmd = [sys.executable, "-I", S1] + (["--slot", slot] if slot else []) + list(args)
    p = subprocess.run(cmd, capture_output=True, text=True, timeout=timeout)
    return p.returncode, (p.stdout or "") + (p.stderr or "")


def check(name, ok, detail=""):
    results.append(ok)
    print(("PASS " if ok else "FAIL ") + name + ("  - " + detail.strip().replace("\n", " | ")[:300] if detail else ""))


def console(slot, words, until):
    code, out = s1(slot, "run", "--until", until, "--max-wait", "8000", *words.split())
    check("console: " + words, code == 0 and re.search(until, out) is not None, out)
    return out


def http(url, token=""):
    req = urllib.request.Request(url)
    if token:
        req.add_header("x-snitch-token", token)
    try:
        with urllib.request.urlopen(req, timeout=5) as r:
            return r.status, r.read().decode("utf-8", "replace")
    except urllib.error.HTTPError as e:
        return e.code, ""
    except Exception as e:
        return -1, repr(e)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--slot", default=None)
    ap.add_argument("--port", type=int, default=6140)
    ap.add_argument("--token", default="", help="ServerToken from MelonPreferences, when one is set")
    a = ap.parse_args()
    slot = a.slot

    console(slot, "snitch help", r"(?i)snitch")
    console(slot, "snitch start", r"(?i)sampl|start")
    console(slot, "snitch top", r"(?i)top|ms|rider|bib|mod")
    console(slot, "snitch stop", r"(?i)stop|sampl")
    console(slot, "snitch report", r"(?i)report|saved|written|\.json|\.md|\.html|snitch")

    # The port is a preference, so it is read from the startup line of this session; --port is the fallback.
    code, startup = s1(slot, "log", "-n", "50000", "--grep", "data server")
    m = re.search(r"data server on http://127\.0\.0\.1:(\d+)/", startup)
    port = int(m.group(1)) if m else a.port
    check("startup log names the data server port", m is not None, f"port={port}")
    base = f"http://127.0.0.1:{port}"
    code, body = http(base + "/health")
    health = None
    try:
        health = json.loads(body)
    except ValueError:
        pass
    check("data server /health answers 200 with JSON", code == 200 and isinstance(health, dict), f"{code} {body[:120]}")
    code, body = http(base + "/snapshot", a.token)
    check("data server /snapshot answers (200, or 401 when a token is set and not given)",
          code == 200 or (code == 401 and not a.token), f"{code} {body[:80]}")
    code, body = http(base + "/caps", a.token)
    check("data server /caps answers (200, or 401 when a token is set and not given)",
          code == 200 or (code == 401 and not a.token), f"{code} {body[:80]}")

    code, log = s1(slot, "log", "-n", "50000", "--grep", "snitch")
    bad = [l for l in log.splitlines() if re.search(r"(?i)\[snitch\]", l) and re.search(r"(?i)error|exception", l)]
    check("log has Snitch lines (grep works)", code == 0 and "[snitch]" in log.lower(), f"{len(log.splitlines())} lines")
    check("no [Snitch] errors or exceptions in this session's log", not bad, "\n".join(bad[-5:]))

    failed = results.count(False)
    print(f"{len(results) - failed}/{len(results)} PASS")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
