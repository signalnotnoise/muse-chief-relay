const test = require("node:test");
const assert = require("node:assert/strict");

let mod;

test.before(async () => {
  mod = await import("../../web/muse/src/workspaceFolder.js");
});

const ASK = "workspace: ship the classroom-agent kit pilot";
const CARD =
  'New workspace: "ship the classroom-agent kit pilot" (asked by @Alex) — saved to the hivemind DB. Agents, pick it up: what\'s the first step?';
const FAIL_CARD =
  '@Alex — tried to create the workspace "ship the classroom-agent kit pilot" but the DB write failed (status 500). I\'ll leave this one for a manual retry.';

function row(id, text, nick) {
  return { id, text, nick: nick || "Fuse", time: "9:00 PM" };
}

test("parseWorkspaceCard parses the canonical goal card", () => {
  assert.deepEqual(mod.parseWorkspaceCard(CARD), {
    goal: "ship the classroom-agent kit pilot",
    requester: "Alex",
  });
});

test("parseWorkspaceCard returns null for non-cards", () => {
  assert.equal(mod.parseWorkspaceCard(ASK), null);
  assert.equal(mod.parseWorkspaceCard(FAIL_CARD), null);
  assert.equal(mod.parseWorkspaceCard("hello world"), null);
  assert.equal(mod.parseWorkspaceCard(""), null);
  assert.equal(mod.parseWorkspaceCard(null), null);
});

test("normalizeGoal collapses whitespace and case", () => {
  assert.equal(
    mod.normalizeGoal("  Ship\n the  CLASSROOM-agent kit pilot "),
    "ship the classroom-agent kit pilot"
  );
  assert.equal(mod.normalizeGoal(null), "");
});

test("deriveWorkspaces merges an ask line and its goal card", () => {
  const ws = mod.deriveWorkspaces([row("a1", ASK, "Alex"), row("c1", CARD, "watcher")]);
  assert.equal(ws.length, 1);
  assert.equal(ws[0].goal, "ship the classroom-agent kit pilot");
  assert.equal(ws[0].askId, "a1");
  assert.equal(ws[0].cardId, "c1");
  assert.equal(ws[0].requester, "Alex");
});

test("deriveWorkspaces keeps an ask with no card as pending", () => {
  const ws = mod.deriveWorkspaces([row("a1", ASK, "Alex")]);
  assert.equal(ws.length, 1);
  assert.equal(ws[0].cardId, null);
  assert.equal(ws[0].askId, "a1");
});

test("deriveWorkspaces dedups repeated asks of the same goal", () => {
  const ws = mod.deriveWorkspaces([
    row("a1", ASK, "Alex"),
    row("a2", "Workspace: Ship the classroom-agent kit pilot", "Alex"),
    row("c1", CARD, "watcher"),
  ]);
  assert.equal(ws.length, 1);
  assert.equal(ws[0].askId, "a1");
  assert.equal(ws[0].cardId, "c1");
});

test("deriveWorkspaces returns most-recent-first and skips non-text rows", () => {
  const ws = mod.deriveWorkspaces([
    row("a1", ASK, "Alex"),
    null,
    { id: "x", nick: "Fuse" },
    row("a2", "workspace: fix the onboarding flow", "chief"),
  ]);
  assert.equal(ws.length, 2);
  assert.equal(ws[0].goal, "fix the onboarding flow");
  assert.equal(ws[1].goal, "ship the classroom-agent kit pilot");
});

test("messageInWorkspace admits the ask, the card, and goal-quoting rows", () => {
  const ws = mod.deriveWorkspaces([row("a1", ASK, "Alex"), row("c1", CARD, "watcher")])[0];
  assert.equal(mod.messageInWorkspace(row("a1", ASK, "Alex"), ws), true);
  assert.equal(mod.messageInWorkspace(row("c1", CARD, "watcher"), ws), true);
  assert.equal(
    mod.messageInWorkspace(
      row("m1", "first step for ship the classroom-agent kit pilot: roster the agents", "chief"),
      ws
    ),
    true
  );
});

test("messageInWorkspace rejects unrelated rows", () => {
  const ws = mod.deriveWorkspaces([row("a1", ASK, "Alex"), row("c1", CARD, "watcher")])[0];
  assert.equal(mod.messageInWorkspace(row("m2", "lunch plans?", "Design"), ws), false);
  assert.equal(mod.messageInWorkspace(row("m3", "the pilot episode was great", "chief"), ws), false);
  assert.equal(mod.messageInWorkspace(null, ws), false);
  assert.equal(mod.messageInWorkspace(row("m2", "hi", "Design"), null), false);
});
