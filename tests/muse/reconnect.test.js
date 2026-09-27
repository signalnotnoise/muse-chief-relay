const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("fs");
const path = require("path");

const docs = require("../../docs/muse/reconnect.js");

test("an established session retries every join warn, including ones that are not nick-taken", () => {
  assert.equal(docs.onJoinWarn("Channel is full", true, 0), "retry");
  assert.equal(docs.onJoinWarn("Internal error", true, 9), "retry");
  assert.equal(docs.onJoinWarn("Nickname taken", true, 99), "retry");
  assert.equal(docs.onJoinWarn("You are joining channels too fast. Wait a moment and try again.", true, 5), "retry");
  assert.equal(docs.onJoinWarn("", true, 3), "retry");
});

test("a first join still stops on bad input, and after 3 taken-nick or rate-limit retries", () => {
  const invalid = "Nickname must consist of up to 24 letters, numbers, and underscores";
  assert.equal(docs.onJoinWarn(invalid, false, 0), "stop");
  assert.equal(docs.onJoinWarn("Nickname taken", false, 0), "retry");
  assert.equal(docs.onJoinWarn("Nickname taken", false, 2), "retry");
  assert.equal(docs.onJoinWarn("Nickname taken", false, 3), "stop");
  assert.equal(docs.onJoinWarn("You are joining channels too fast. Wait a moment.", false, 1), "retry");
  assert.equal(docs.onJoinWarn("You are joining channels too fast. Wait a moment.", false, 3), "stop");
});

test("a dropped socket keeps retrying until the user disconnects, even before the first join", () => {
  assert.equal(docs.onSocketClose(true), "retry");
  assert.equal(docs.onSocketClose(false), "stop");
});

test("socket closes do not spend the first-join warn budget", () => {
  // The page used to pass the shared backoff counter into onJoinWarn, so three
  // drops before onlineSet made the next "nick taken" stop a first join.
  let warns = 0;
  for (let i = 0; i < 3; i++) {
    assert.equal(docs.onSocketClose(true), "retry");
    const taken = docs.recordJoinWarn("Nickname taken", false, warns);
    assert.equal(taken.decision, "retry");
    warns = taken.firstJoinWarns;
  }
  assert.equal(warns, 3);
  const fourth = docs.recordJoinWarn("Nickname taken", false, warns);
  assert.equal(fourth.decision, "stop");
  assert.equal(fourth.firstJoinWarns, 3);

  const established = docs.recordJoinWarn("Channel is full", true, 9);
  assert.equal(established.decision, "retry");
  assert.equal(established.firstJoinWarns, 9);

  const invalid = docs.recordJoinWarn(
    "Nickname must consist of up to 24 letters, numbers, and underscores",
    false,
    0);
  assert.equal(invalid.decision, "stop");
  assert.equal(invalid.firstJoinWarns, 0);
});

test("docs/muse and web/muse reconnect.js are the same file", () => {
  const a = fs.readFileSync(path.join(__dirname, "../../docs/muse/reconnect.js"));
  const b = fs.readFileSync(path.join(__dirname, "../../web/muse/reconnect.js"));
  assert.deepEqual(a, b);
});
