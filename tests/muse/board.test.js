const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("fs");
const path = require("path");

const board = require("../../web/muse/board.js");

const root = path.join(__dirname, "../..");
function read(rel) {
  return fs.readFileSync(path.join(root, rel));
}

test("docs/muse and web/muse stay identical, and the Pages board matches", () => {
  for (const name of ["board.js", "index.html", "styles.css", "app.js", "reconnect.js"]) {
    assert.deepEqual(read("docs/muse/" + name), read("web/muse/" + name), name);
  }
  assert.deepEqual(
    read("docs/boards/fuse-grok-6f4e970cd8.jsonl"),
    read("boards/fuse-grok-6f4e970cd8.jsonl"));
});

test("the page fetches the room file next to Muse, then the repo-root file", () => {
  assert.deepEqual(board.BOARD_URLS, [
    "../boards/fuse-grok-6f4e970cd8.jsonl",
    "../../boards/fuse-grok-6f4e970cd8.jsonl",
  ]);
});

test("the seeded room file renders as one decision and one open task", () => {
  const text = read("boards/fuse-grok-6f4e970cd8.jsonl").toString("utf8");
  const view = board.viewBoard(text);
  assert.equal(view.status, "ok");
  assert.equal(view.skipped, 0);
  assert.equal(view.scratch.length, 0);
  assert.equal(view.decisions.length, 1);
  assert.equal(view.decisions[0].decider, "Fuse");
  assert.equal(view.tasks.length, 1);
  assert.equal(view.tasks[0].id, 1);
  assert.equal(view.tasks[0].state, "open");
  assert.equal(view.tasks[0].owner, "Alex");
  assert.match(view.tasks[0].title, /teaching-kit/);
  assert.equal(board.noteFor(view), "");
});

test("blank and empty files are empty, and a bad line does not drop the good ones", () => {
  assert.equal(board.viewBoard("").status, "empty");
  assert.equal(board.viewBoard("\n\n").status, "empty");
  assert.equal(board.noteFor(board.viewBoard("")), board.NOTES.empty);

  const mixed = [
    "{\"type\":\"task\",\"id\":2,\"title\":\"keep me\",\"owner\":\"Alex\",\"state\":\"blocked\",\"blocked_on\":\"card\",\"handoff_to\":\"Fuse\"}",
    "not json",
    "{\"type\":\"nope\"}",
    "",
    "{\"type\":\"scratch\",\"ts\":1790514400,\"author\":\"Fuse\",\"text\":\"a note\"}",
    "{\"type\":\"decision\",\"ts\":1790514400,\"decider\":\"Fuse\",\"decision\":\"ship it\",\"context\":\"v0\"}",
  ].join("\n");
  const view = board.viewBoard(mixed);
  assert.equal(view.status, "ok");
  assert.equal(view.skipped, 2);
  assert.equal(view.tasks.length, 1);
  assert.equal(view.tasks[0].state, "blocked");
  assert.equal(view.tasks[0].blocked_on, "card");
  assert.equal(view.scratch.length, 1);
  assert.equal(view.decisions.length, 1);
  assert.equal(board.noteFor(view), board.NOTES.partial);
  assert.equal(board.viewBoard("{").status, "unreadable");
  assert.equal(board.noteFor(board.viewBoard("[]")), board.NOTES.unreadable);
});

test("fetch uses the first real jsonl and treats a miss as unavailable", async () => {
  const calls = [];
  function fake(map) {
    return function (url) {
      calls.push(url);
      const hit = map[url];
      if (!hit) return Promise.reject(new Error("network"));
      if (hit.throw) return Promise.reject(hit.throw);
      return Promise.resolve({
        ok: hit.ok !== false,
        headers: { get: () => hit.type || "" },
        text: () => Promise.resolve(hit.body || ""),
      });
    };
  }

  const seeded = read("boards/fuse-grok-6f4e970cd8.jsonl").toString("utf8");
  calls.length = 0;
  const first = await board.fetchBoardText(fake({
    "../boards/fuse-grok-6f4e970cd8.jsonl": { ok: false },
    "../../boards/fuse-grok-6f4e970cd8.jsonl": { ok: true, body: seeded, type: "text/plain" },
  }));
  assert.equal(first.status, "ok");
  assert.equal(first.text, seeded);
  assert.deepEqual(calls, board.BOARD_URLS);

  calls.length = 0;
  const htmlFirst = await board.fetchBoardText(fake({
    "../boards/fuse-grok-6f4e970cd8.jsonl": { ok: true, type: "text/html", body: "<html></html>" },
    "../../boards/fuse-grok-6f4e970cd8.jsonl": { ok: true, body: "", type: "application/octet-stream" },
  }));
  assert.equal(htmlFirst.status, "ok");
  assert.equal(htmlFirst.text, "");
  assert.equal(board.viewBoard(htmlFirst.text).status, "empty");

  calls.length = 0;
  const emptyWins = await board.fetchBoardText(fake({
    "../boards/fuse-grok-6f4e970cd8.jsonl": { ok: true, body: "", type: "text/plain" },
    "../../boards/fuse-grok-6f4e970cd8.jsonl": { ok: true, body: seeded },
  }));
  assert.equal(emptyWins.status, "ok");
  assert.equal(emptyWins.text, "");
  assert.deepEqual(calls, [board.BOARD_URLS[0]]);

  const missing = await board.fetchBoardText(fake({}));
  assert.equal(missing.status, "missing");
});

test("the board client does not store a password or write the file", () => {
  const src = read("web/muse/board.js").toString("utf8");
  assert.equal(src.includes("password"), false);
  assert.equal(src.includes("localStorage"), false);
  assert.equal(src.includes("method:"), false);
});
