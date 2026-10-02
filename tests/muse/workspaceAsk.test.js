const test = require("node:test");
const assert = require("node:assert/strict");

let mod;

test.before(async () => {
  mod = await import("../../web/muse/src/workspaceAsk.js");
});

test("buildWorkspaceAsk emits the canonical goal-first line", () => {
  assert.equal(
    mod.buildWorkspaceAsk("ship the classroom-agent kit pilot"),
    "workspace: ship the classroom-agent kit pilot"
  );
});

test("buildWorkspaceAsk trims and collapses whitespace", () => {
  assert.equal(
    mod.buildWorkspaceAsk("  fix\n the\r\n  onboarding\tflow "),
    "workspace: fix the onboarding flow"
  );
});

test("buildWorkspaceAsk returns null for empty goals", () => {
  assert.equal(mod.buildWorkspaceAsk(""), null);
  assert.equal(mod.buildWorkspaceAsk("   \n  "), null);
  assert.equal(mod.buildWorkspaceAsk(null), null);
  assert.equal(mod.buildWorkspaceAsk(undefined), null);
});

test("parseWorkspaceAsk round-trips the canonical line", () => {
  const goal = "ship the classroom-agent kit pilot";
  assert.equal(mod.parseWorkspaceAsk(mod.buildWorkspaceAsk(goal)), goal);
});

test("parseWorkspaceAsk tolerates casing and padding on the prefix", () => {
  assert.equal(mod.parseWorkspaceAsk("Workspace:  tidy the board"), "tidy the board");
  assert.equal(mod.parseWorkspaceAsk("  workspace: x  "), "x");
});

test("parseWorkspaceAsk rejects non-ask lines", () => {
  assert.equal(mod.parseWorkspaceAsk("hello workspace: hi"), null);
  assert.equal(mod.parseWorkspaceAsk("workspace:"), null);
  assert.equal(mod.parseWorkspaceAsk("workspace:   "), null);
  assert.equal(mod.parseWorkspaceAsk("just chatting"), null);
  assert.equal(mod.parseWorkspaceAsk(""), null);
});
