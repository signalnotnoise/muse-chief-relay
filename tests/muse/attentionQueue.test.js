const test = require("node:test");
const assert = require("node:assert/strict");

let aq;

test.before(async () => {
  aq = await import("../../web/muse/src/attentionQueue.js");
});

function task(id, title, state, extra) {
  return Object.assign(
    { type: "task", id, title, owner: "chief", state: state || "open" },
    extra,
  );
}

function board(tasks) {
  return { tasks: tasks || [], decisions: [], scratch: [], skipped: 0 };
}

test("an empty or missing board yields no items", () => {
  assert.deepEqual(aq.deriveAttentionItems(null), []);
  assert.deepEqual(aq.deriveAttentionItems(undefined), []);
  assert.deepEqual(aq.deriveAttentionItems(board()), []);
  assert.deepEqual(aq.deriveAttentionItems({}), []);
});

test("only blocked tasks that name Alex become items", () => {
  const items = aq.deriveAttentionItems(
    board([
      task(1, "merge the relay PR", "blocked", { blocked_on: "Alex" }),
      task(2, "merge the relay PR", "open", { blocked_on: "Alex" }),
      task(3, "merge the relay PR", "claimed", { blocked_on: "Alex" }),
      task(4, "merge the relay PR", "done", { blocked_on: "Alex" }),
      task(5, "merge the relay PR", "blocked", { blocked_on: "chief" }),
    ]),
  );
  assert.equal(items.length, 1);
  assert.equal(items[0].key, "task:1");
});

test("Alex is found in handoff_to, owner, and the title", () => {
  const via = aq.deriveAttentionItems(
    board([
      task(1, "pick a ship date", "blocked", { handoff_to: "@Alex" }),
      task(2, "pick a ship date", "blocked", { blocked_on: "chief" }),
    ]),
  );
  assert.equal(via.length, 1);
  assert.equal(via[0].key, "task:1");

  const owned = aq.deriveAttentionItems(
    board([task(1, "pick a ship date", "blocked", { owner: "Alex" })]),
  );
  assert.equal(owned.length, 1);

  const titled = aq.deriveAttentionItems(
    board([task(1, "waiting on Alex to merge #52", "blocked", { blocked_on: "chief" })]),
  );
  assert.equal(titled.length, 1);
});

test("kinds: merge tap, deploy go, decision", () => {
  const items = aq.deriveAttentionItems(
    board([
      task(1, "merge the relay PR", "blocked", { blocked_on: "Alex" }),
      task(2, "deploy the classroom kit", "blocked", { blocked_on: "Alex" }),
      task(3, "pick the product ship order", "blocked", { blocked_on: "Alex" }),
    ]),
  );
  assert.equal(items.length, 3);
  assert.equal(items[0].kind, "merge");
  assert.equal(items[0].kindLabel, "merge tap");
  assert.equal(items[0].ctaLabel, "Open PRs");
  assert.equal(items[1].kind, "deploy");
  assert.equal(items[1].kindLabel, "deploy go");
  assert.equal(items[1].ctaLabel, "Cleared");
  assert.equal(items[2].kind, "decision");
  assert.equal(items[2].kindLabel, "decision");
  assert.equal(items[2].ctaLabel, "Cleared");
});

test("merge is recognized from PR / review / approval language", () => {
  for (const title of [
    "PR #52 needs a merge tap",
    "review the password-join PR",
    "approve the voizle#7 backport",
  ]) {
    const items = aq.deriveAttentionItems(
      board([task(1, title, "blocked", { blocked_on: "Alex" })]),
    );
    assert.equal(items[0].kind, "merge", title);
  }
});

test("the detail names who the task waits on", () => {
  const items = aq.deriveAttentionItems(
    board([task(1, "merge the relay PR", "blocked", { blocked_on: "Alex (merge tap)" })]),
  );
  assert.equal(items[0].detail, "waiting on Alex (merge tap)");
  const bare = aq.deriveAttentionItems(
    board([task(1, "merge the relay PR", "blocked", { owner: "Alex" })]),
  );
  assert.equal(bare[0].detail, "");
});

test("items sort merge, deploy, decision, then by task id", () => {
  const items = aq.deriveAttentionItems(
    board([
      task(9, "pick the ship order", "blocked", { blocked_on: "Alex" }),
      task(7, "deploy the kit", "blocked", { blocked_on: "Alex" }),
      task(8, "merge the PR", "blocked", { blocked_on: "Alex" }),
      task(3, "deploy the relay", "blocked", { blocked_on: "Alex" }),
    ]),
  );
  assert.deepEqual(
    items.map((i) => i.key),
    ["task:8", "task:3", "task:7", "task:9"],
  );
});

test("non-task records and malformed tasks are ignored", () => {
  const items = aq.deriveAttentionItems({
    tasks: [
      { type: "decision", ts: 1, decider: "Alex", decision: "ship it" },
      { type: "task", id: 1, title: "merge it", owner: "chief", state: "blocked", blocked_on: "Alex" },
      null,
      "nope",
    ],
    decisions: [],
    scratch: [],
    skipped: 0,
  });
  assert.equal(items.length, 1);
  assert.equal(items[0].key, "task:1");
});
