const test = require("node:test");
const assert = require("node:assert/strict");

let mod;

test.before(async () => {
  mod = await import("../../web/muse/src/composerHistory.js");
});

function history(limit) {
  return mod.createComposerHistory(limit === undefined ? 50 : limit);
}

test("no history means no browsing: arrows keep native behavior", () => {
  const h = history();
  assert.equal(h.step(-1, "draft"), null);
  assert.equal(h.step(1, "draft"), null);
  assert.equal(h.browsing(), false);
});

test("ArrowUp walks older, ArrowDown walks newer, then restores the draft", () => {
  const h = history();
  h.push("first");
  h.push("second");
  h.push("third");
  assert.equal(h.step(-1, "unsent draft"), "third");
  assert.equal(h.browsing(), true);
  assert.equal(h.step(-1, "third"), "second");
  assert.equal(h.step(-1, "second"), "first");
  // Clamped at the oldest; pressing up again stays put.
  assert.equal(h.step(-1, "first"), "first");
  assert.equal(h.step(1, "first"), "second");
  assert.equal(h.step(1, "second"), "third");
  // Past the newest: back to the unsent draft, browsing ends.
  assert.equal(h.step(1, "third"), "unsent draft");
  assert.equal(h.browsing(), false);
  // ArrowDown with nothing to move toward is a no-op.
  assert.equal(h.step(1, "unsent draft"), null);
});

test("pushing records what was sent and resets browsing", () => {
  const h = history();
  h.push("one");
  assert.equal(h.step(-1, ""), "one");
  h.push("two");
  assert.equal(h.browsing(), false);
  assert.equal(h.step(-1, ""), "two");
  assert.equal(h.step(-1, "two"), "one");
});

test("blank and consecutive-duplicate sends are not recorded", () => {
  const h = history();
  h.push("   ");
  h.push("");
  assert.equal(h.size(), 0);
  h.push("hey");
  h.push("hey");
  h.push("  hey  ");
  assert.equal(h.size(), 1);
  h.push("other");
  assert.equal(h.size(), 2);
});

test("history is capped at the limit, oldest first", () => {
  const h = history(3);
  h.push("a");
  h.push("b");
  h.push("c");
  h.push("d");
  assert.equal(h.size(), 3);
  assert.equal(h.step(-1, ""), "d");
  assert.equal(h.step(-1, "d"), "c");
  assert.equal(h.step(-1, "c"), "b");
  assert.equal(h.step(-1, "b"), "b");
});

test("cancel abandons browsing without touching the box", () => {
  const h = history();
  h.push("one");
  assert.equal(h.step(-1, "typing…"), "one");
  h.cancel();
  assert.equal(h.browsing(), false);
  // A fresh ArrowUp starts over from the newest, not the abandoned spot.
  h.push("two");
  assert.equal(h.step(-1, "typing…"), "two");
});
