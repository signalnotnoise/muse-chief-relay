const test = require("node:test");
const assert = require("node:assert/strict");
const { execFileSync } = require("node:child_process");
const path = require("node:path");

const ROOT = path.join(__dirname, "../..");

test("the public leak guard passes on the committed tree", () => {
  const out = execFileSync("node", ["tools/check-public-leaks.mjs"], {
    cwd: ROOT,
    encoding: "utf8",
  });
  assert.match(out, /public leak guard: ok/);
});
