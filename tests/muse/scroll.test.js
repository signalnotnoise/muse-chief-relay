const test = require("node:test");
const assert = require("node:assert/strict");

let scroll;

test.before(async () => {
  scroll = await import("../../web/muse/src/scroll.js");
});

test("a missing transcript follows the tail", () => {
  assert.equal(scroll.isNearBottom(null), true);
});

test("the bottom and a small gap still follow", () => {
  assert.equal(scroll.isNearBottom({ scrollHeight: 400, scrollTop: 200, clientHeight: 200 }), true);
  assert.equal(scroll.isNearBottom({ scrollHeight: 480, scrollTop: 200, clientHeight: 200 }), true);
  assert.equal(scroll.NEAR_BOTTOM_PX, 80);
});

test("reading history does not follow", () => {
  assert.equal(scroll.isNearBottom({ scrollHeight: 2000, scrollTop: 0, clientHeight: 400 }), false);
  assert.equal(scroll.isNearBottom({ scrollHeight: 481, scrollTop: 200, clientHeight: 200 }), false);
});
