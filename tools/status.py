#!/usr/bin/env python3
"""Build a public task-status view from a relay inbox.jsonl.

Only tasks explicitly tagged with a repo listed in config "publish_repos"
are published. Untagged tasks, and tasks tagged with anything else, are left
out (fail closed). A result can't change a task's visibility: it inherits the
task's repo.

Only messages carrying a hack.chat tripcode listed in "publish_trips" count,
including the bridge's own messages (give the bridge a trip with "pass").
An empty or missing publish_trips publishes nothing.

Shortcut tasks ("TASK to chief: ...") carry no id or repo, so they never
appear in the view.

Usage:
  python3 tools/status.py [--config config.json] [--inbox PATH] [--out docs/status.json]
"""
from __future__ import annotations

import argparse
import json
import re
import sys
from datetime import datetime, timezone
from pathlib import Path

DEFAULT_PUBLISH = ["signalnotnoise/muse-chief-relay"]
SHORT_TASK = re.compile(r"(?i)^TASK\s+to\s+(chief|muse)\s*:\s*(.+?)(?:\s+[—\-]\s+|\s*:\s*)(.+)$")
SHORT_RESULT = re.compile(r"(?i)^RESULT\s+(\S+)\s*:\s*(.+)$")


def iso(ts: float) -> str:
    return datetime.fromtimestamp(ts, timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z")


def parse(text: str) -> dict | None:
    t = (text or "").strip()
    if t.startswith("{") and t.endswith("}"):
        try:
            obj = json.loads(t)
            if isinstance(obj, dict) and isinstance(obj.get("type"), str):
                return obj
        except ValueError:
            return None
        return None
    m = SHORT_TASK.match(t)
    if m:
        return {"type": "task", "to": m.group(1).lower(), "title": m.group(2).strip(), "body": m.group(3).strip()}
    m = SHORT_RESULT.match(t)
    if m:
        return {"type": "result", "id": m.group(1), "status": "done", "summary": m.group(2).strip()}
    return None


def load_config(path: Path | None) -> dict:
    if path and path.exists():
        return json.loads(path.read_text())
    return {}


def build(inbox: Path, cfg: dict) -> dict:
    # Missing or null uses the default; an explicit [] publishes nothing.
    raw_publish = cfg.get("publish_repos")
    publish = {r.lower() for r in (DEFAULT_PUBLISH if raw_publish is None else raw_publish) if isinstance(r, str)}
    trips = {t for t in (cfg.get("publish_trips") or []) if isinstance(t, str) and t}

    tasks: dict[str, dict] = {}
    # Coverage: each outbound join starts a session that runs until the last
    # frame before the next join. Quiet stretches inside a session count as
    # observed; a silent drop that never rejoined can't be detected.
    sessions: list[list[float]] = []

    for line in inbox.read_text().splitlines():
        try:
            row = json.loads(line)
        except ValueError:
            continue
        ts = float(row.get("ts") or 0)
        msg = row.get("msg") or {}
        if ts:
            if (row.get("dir") == "out" and isinstance(msg, dict) and msg.get("cmd") == "join") or not sessions:
                sessions.append([ts, ts])
            else:
                sessions[-1][1] = ts
        # Only inbound chat: the server echo is the canonical record of what was said.
        if row.get("dir") != "in" or not isinstance(msg, dict) or msg.get("cmd") != "chat":
            continue
        nick = msg.get("nick") or ""
        trip = msg.get("trip") or ""
        # Every publisher needs a listed trip, the bridge included; no nick bypass.
        if trip not in trips:
            continue
        env = parse(msg.get("text", ""))
        if not env:
            continue
        kind = env.get("type")
        tid = str(env.get("id") or "").strip()
        if kind == "task" and tid:
            if tid in tasks:
                continue  # first task with an id wins; later reuse is ignored
            tasks[tid] = {
                "id": tid,
                "title": str(env.get("title") or "")[:200],
                "to": env.get("to"),
                "from": nick,
                # Arbitrary JSON: anything that isn't a string counts as untagged.
                "repo": env.get("repo") if isinstance(env.get("repo"), str) else None,
                "status": "open",
                "created": iso(ts),
                "updated": iso(ts),
            }
        elif kind == "ack" and tid in tasks and tasks[tid]["status"] == "open":
            tasks[tid]["status"] = "acked"
            tasks[tid]["updated"] = iso(ts)
        elif kind == "result" and tid in tasks:
            status = str(env.get("status") or "done")
            tasks[tid]["status"] = status if status in {"done", "blocked", "rejected"} else "done"
            tasks[tid]["summary"] = str(env.get("summary") or "")[:500]
            tasks[tid]["updated"] = iso(ts)

    published = [t for t in tasks.values() if isinstance(t.get("repo"), str) and t["repo"].lower() in publish]
    published.sort(key=lambda t: t["created"], reverse=True)

    coverage = [{"from": iso(a), "to": iso(b)} for a, b in sessions]

    return {
        "generated": iso(datetime.now(timezone.utc).timestamp()),
        "publish_repos": sorted(publish),
        "note": "Only tasks tagged with a listed repo are shown. Coverage lists the windows this log observed; anything said outside them is missing.",
        "coverage": coverage,
        "counts": {s: sum(1 for t in published if t["status"] == s) for s in ("open", "acked", "done", "blocked", "rejected")},
        "tasks": published,
    }


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--config", type=Path, default=Path("config.json"))
    ap.add_argument("--inbox", type=Path)
    ap.add_argument("--out", type=Path, default=Path("docs/status.json"))
    args = ap.parse_args()

    cfg = load_config(args.config)
    inbox = args.inbox or Path(cfg.get("base", ".")) / "inbox.jsonl"
    if not inbox.exists():
        print(f"no inbox at {inbox}", file=sys.stderr)
        return 1
    out = build(inbox, cfg)
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(out, indent=2) + "\n")
    print(f"wrote {args.out}: {len(out['tasks'])} published task(s)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
