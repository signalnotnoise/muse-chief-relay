#!/usr/bin/env python3
"""Tests for tools/status.py. Run: python3 tools/test_status.py"""
import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import status  # noqa: E402

PUB = "signalnotnoise/muse-chief-relay"


def chat(ts, nick, text, trip=""):
    return json.dumps({"ts": ts, "dir": "in", "msg": {"cmd": "chat", "nick": nick, "trip": trip, "text": text}})


def task(tid, repo=None, title="t"):
    d = {"type": "task", "id": tid, "to": "chief", "title": title, "body": "b"}
    if repo:
        d["repo"] = repo
    return json.dumps(d)


class StatusTests(unittest.TestCase):
    def run_build(self, lines, cfg=None):
        with tempfile.TemporaryDirectory() as d:
            p = Path(d) / "inbox.jsonl"
            p.write_text("\n".join(lines) + "\n")
            return status.build(p, cfg or {})

    def test_untagged_task_is_not_published(self):
        out = self.run_build([chat(1, "Fuse", task("a"))])
        self.assertEqual(out["tasks"], [])

    def test_private_repo_task_is_not_published(self):
        out = self.run_build([chat(1, "Fuse", task("a", "n0obscertified/voizle", "secret thing"))])
        self.assertEqual(out["tasks"], [])
        self.assertNotIn("secret thing", json.dumps(out))

    def test_result_cannot_upgrade_visibility(self):
        out = self.run_build([
            chat(1, "Fuse", task("a")),
            chat(2, "chief", json.dumps({"type": "result", "id": "a", "status": "done", "summary": "x", "repo": PUB})),
        ])
        self.assertEqual(out["tasks"], [])

    def test_lifecycle(self):
        out = self.run_build([
            chat(1, "Fuse", task("a", PUB)),
            chat(2, "chief", json.dumps({"type": "ack", "id": "a", "from": "chief"})),
        ])
        self.assertEqual(out["tasks"][0]["status"], "acked")
        out = self.run_build([
            chat(1, "Fuse", task("a", PUB)),
            chat(2, "chief", json.dumps({"type": "ack", "id": "a"})),
            chat(3, "chief", json.dumps({"type": "result", "id": "a", "status": "blocked", "summary": "why"})),
        ])
        self.assertEqual(out["tasks"][0]["status"], "blocked")
        self.assertEqual(out["counts"]["blocked"], 1)

    def test_allowlist_from_config(self):
        cfg = {"publish_repos": ["someone/fork"]}
        out = self.run_build([chat(1, "Fuse", task("a", PUB)), chat(2, "Fuse", task("b", "someone/fork"))], cfg)
        self.assertEqual([t["id"] for t in out["tasks"]], ["b"])

    def test_trip_filter(self):
        cfg = {"publish_trips": ["EtBBNv"]}
        out = self.run_build([
            chat(1, "Fuse", task("a", PUB), trip="EtBBNv"),
            chat(2, "Fuse", task("b", PUB), trip=""),
        ], cfg)
        self.assertEqual([t["id"] for t in out["tasks"]], ["a"])

    def test_duplicate_task_id_first_wins(self):
        out = self.run_build([chat(1, "Fuse", task("a", PUB, "first")), chat(2, "x", task("a", PUB, "spoof"))])
        self.assertEqual(out["tasks"][0]["title"], "first")

    def test_coverage_splits_on_rejoin_not_quiet(self):
        join = lambda ts: json.dumps({"ts": ts, "dir": "out", "msg": {"cmd": "join", "channel": "c", "nick": "chief"}})
        out = self.run_build([join(1), chat(2, "Fuse", "hi"), chat(2 + 3600, "Fuse", "quiet hour, still connected"),
                              join(10000), chat(10001, "Fuse", "hi")])
        self.assertEqual(len(out["coverage"]), 2)
        self.assertEqual(out["coverage"][0]["to"], status.iso(3602))


if __name__ == "__main__":
    unittest.main()
