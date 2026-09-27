const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("fs");
const path = require("path");

const board = require("../../web/muse/board.js");

const SEEDED = path.join(__dirname, "../../boards/fuse-grok-6f4e970cd8.jsonl");

function task(id, title, state) {
  return JSON.stringify({ type: "task", id: id, title: title, owner: "Alex", state: state || "open" });
}

test("the seeded room board is a decision plus open task #1", () => {
  const text = fs.readFileSync(SEEDED, "utf8");
  assert.ok(text.endsWith("\n"), "seeded file ends with a newline, so its last line is complete");
  const parsed = board.parseBoard(text);
  assert.equal(parsed.skipped, 0);
  assert.equal(parsed.scratch.length, 0);
  assert.equal(parsed.decisions.length, 1);
  assert.equal(parsed.decisions[0].decider, "Fuse");
  assert.match(parsed.decisions[0].decision, /Room board created/);
  assert.equal(parsed.tasks.length, 1);
  assert.equal(parsed.tasks[0].id, 1);
  assert.equal(parsed.tasks[0].owner, "Alex");
  assert.equal(parsed.tasks[0].state, "open");
  assert.equal(parsed.tasks[0].title, "teaching-kit card — Alex picks the workload");
});

test("a trailing partial line is not a record and does not replace the last complete one", () => {
  const partial = task(1, "complete card", "open") + "\n" + task(1, "still being written", "claimed");
  assert.ok(!partial.endsWith("\n"));
  const parsed = board.parseBoard(partial);
  assert.equal(parsed.tasks.length, 1);
  assert.equal(parsed.tasks[0].title, "complete card");
  assert.equal(parsed.tasks[0].state, "open");
  assert.equal(parsed.skipped, 0);
});

test("a later complete task line with the same id replaces the card", () => {
  const text = [
    task(1, "first", "open"),
    task(2, "other", "open"),
    task(1, "updated", "blocked"),
    ""
  ].join("\n");
  const parsed = board.parseBoard(text);
  assert.equal(parsed.tasks.length, 2);
  assert.equal(parsed.tasks[0].title, "updated");
  assert.equal(parsed.tasks[0].state, "blocked");
  assert.equal(parsed.tasks[1].id, 2);
});

test("malformed, unknown, and incomplete lines are skipped, not shown", () => {
  const text = [
    "not json",
    JSON.stringify({ type: "nope" }),
    JSON.stringify({ type: "task", id: "1", title: "bad id", owner: "Alex", state: "open" }),
    JSON.stringify({ type: "scratch", ts: 10, author: "Fuse", text: "a note", pass: "secret" }),
    ""
  ].join("\n");
  const parsed = board.parseBoard(text);
  assert.equal(parsed.skipped, 3);
  assert.equal(parsed.scratch.length, 1);
  assert.equal(parsed.scratch[0].text, "a note");
  assert.equal(parsed.scratch[0].pass, undefined);
  assert.deepEqual(Object.keys(parsed.scratch[0]).sort(), ["author", "text", "ts", "type"]);
});

test("an empty file and a file with no newline yield no records", () => {
  assert.deepEqual(board.parseBoard(""), { tasks: [], decisions: [], scratch: [], skipped: 0 });
  assert.deepEqual(board.parseBoard("{\"type\":\"scratch\",\"ts\":1,\"author\":\"a\",\"text\":\"x\"}"), {
    tasks: [], decisions: [], scratch: [], skipped: 0
  });
});

test("sources are the repo-relative file, then GitHub raw of main", () => {
  const page = "https://signalnotnoise.github.io/muse-chief-relay/muse/index.html";
  const sources = board.boardSources(page, 0);
  assert.equal(sources.length, 2);
  assert.equal(sources[0], "https://signalnotnoise.github.io/" + board.BOARD_FILE);
  assert.equal(sources[1], board.RAW_URL);
  assert.match(board.RAW_URL, /\/main\/boards\/fuse-grok-6f4e970cd8\.jsonl$/);
  const busted = board.boardSources("http://127.0.0.1:8080/docs/muse/index.html", 5);
  assert.equal(busted[0], "http://127.0.0.1:8080/" + board.BOARD_FILE + "?t=5");
  assert.equal(busted[1], board.RAW_URL + "?t=5");
});

test("the client does not default a channel or take ownership of the board file", () => {
  const html = fs.readFileSync(path.join(__dirname, "../../web/muse/index.html"), "utf8");
  const channel = html.match(/<input id="channel"[^>]*>/)[0];
  assert.match(channel, /placeholder="your-channel-name"/);
  assert.doesNotMatch(channel, /\svalue=/);
  assert.match(html, /id="room-board"/);
  assert.match(html, new RegExp(board.BOARD_FILE.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")));
  const src = fs.readFileSync(path.join(__dirname, "../../web/muse/board.js"), "utf8");
  assert.doesNotMatch(src, /localStorage|sessionStorage|method:\s*["'](PUT|POST|PATCH)|writeFile|appendFile/);
  assert.equal(board.BOARD_FILE, "boards/fuse-grok-6f4e970cd8.jsonl");
});

test("docs/muse and web/muse are the same files", () => {
  const docsDir = path.join(__dirname, "../../docs/muse");
  const webDir = path.join(__dirname, "../../web/muse");
  assert.deepEqual(fs.readdirSync(docsDir).sort(), fs.readdirSync(webDir).sort());
  for (const name of fs.readdirSync(webDir)) {
    assert.deepEqual(
      fs.readFileSync(path.join(docsDir, name)),
      fs.readFileSync(path.join(webDir, name)),
      name);
  }
  assert.deepEqual(
    fs.readFileSync(path.join(__dirname, "../../docs/muse/board.js")),
    fs.readFileSync(path.join(__dirname, "../../web/muse/board.js")));
});
