#!/usr/bin/python3
"""One-shot SSH control for the registered, non-autostart RVC LaunchAgent.

Install as ~/.local/bin/rvc-server-control (mode 700). No token, private key,
listener or resident controller is required. Only fixed start/stop/state verbs
are allowed; launchctl targets the logged-in user's explicit GUI domain.
"""
import json
import os
from pathlib import Path
import re
import subprocess
import sys

LABEL = "dev.lingxi.rvcServer"
TARGET = f"gui/{os.getuid()}/{LABEL}"


def run(*args):
    return subprocess.run(
        ["/bin/launchctl", *args], capture_output=True, text=True, timeout=4
    )


def state():
    result = run("print", TARGET)
    registered = result.returncode == 0
    # A registered but idle task has no pid. Do not match arbitrary Pythons.
    pid = re.search(r"^\s*pid = ([1-9][0-9]*)\s*$", result.stdout, re.MULTILINE)
    wrapper = Path.home() / ".local/bin/rvc-server-launch"
    return {
        "serverRunning": registered and bool(pid),
        "serviceRegistered": registered,
        "controllable": registered and wrapper.is_file(),
        "controlMode": "ssh",
        "label": LABEL,
        "wrapperPresent": wrapper.is_file(),
    }


def main():
    action = sys.argv[1] if len(sys.argv) == 2 else ""
    if action not in {"state", "start", "stop"}:
        print(json.dumps({"error": "unsupported action"}))
        return 2
    try:
        before = state()
        if action == "state":
            print(json.dumps(before))
            return 0
        if not before["controllable"]:
            raise RuntimeError("RVC LaunchAgent or launch wrapper is not installed")
        result = None
        if action == "start" and not before["serverRunning"]:
            result = run("kickstart", TARGET)
        elif action == "stop" and before["serverRunning"]:
            result = run("kill", "SIGTERM", TARGET)
        print(json.dumps({
            "action": action,
            "exitCode": result.returncode if result else 0,
            "stdout": "",
            "stderr": result.stderr.strip()[:240] if result else "",
            "serverRunning": state()["serverRunning"],
        }))
        return 0
    except (OSError, RuntimeError, subprocess.TimeoutExpired) as exc:
        print(json.dumps({"error": str(exc)[:240]}))
        return 1


if __name__ == "__main__":
    sys.exit(main())
