const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("fs");
const path = require("path");

const board = require("../../docs/muse/board.js");

const repoRoot = path.join(__dirname, "../..");
const seedPath = path.join(repoRoot, "boards/fuse-grok-6f4e970cd8.jsonl");

test("the seeded room file parses to one open task, one decision, and no scratch", () => {
  const parsed = board.parseJsonl(fs.readFileSync(seedPath, "utf8"));
  assert.equal(parsed.skipped, 0);
  assert.equal(parsed.empty, false);
  assert.equal(parsed.tasks.length, 1);
  assert.equal(parsed.tasks[0].id, 1);
  assert.equal(parsed.tasks[0].state, "open");
  assert.equal(parsed.tasks[0].owner, "Alex");
  assert.match(parsed.tasks[0].title, /teaching-kit card/);
  assert.equal(parsed.decisions.length, 1);
  assert.equal(parsed.decisions[0].decider, "Fuse");
  assert.equal(parsed.scratch.length, 0);
});

test("scratch lines render, a later task id wins, and bad lines are skipped", () => {
  const text = [
    "{\"type\":\"task\",\"id\":1,\"title\":\"first\",\"owner\":\"A\",\"state\":\"open\"}",
    "{\"type\":\"task\",\"id\":1,\"title\":\"second\",\"owner\":\"B\",\"state\":\"claimed\",\"handoff_to\":\"chief\"}",
    "{\"type\":\"scratch\",\"ts\":1790514400,\"author\":\"Fuse\",\"text\":\"note <script>\"}",
    "",
    "not json",
    "{\"type\":\"nope\"}",
    "{\"type\":\"task\",\"id\":\"1\",\"title\":\"string id\",\"owner\":\"A\",\"state\":\"open\"}",
  ].join("\n");
  const parsed = board.parseJsonl(text);
  assert.equal(parsed.tasks.length, 1);
  assert.equal(parsed.tasks[0].title, "second");
  assert.equal(parsed.tasks[0].state, "claimed");
  assert.equal(parsed.tasks[0].handoff_to, "chief");
  assert.equal(parsed.scratch.length, 1);
  assert.equal(parsed.scratch[0].text, "note <script>");
  assert.equal(parsed.skipped, 3);
  const vm = board.viewModel(parsed);
  assert.equal(vm.tasks[0].title, "second");
  assert.equal(vm.tasks[0].state, "claimed");
  assert.match(vm.tasks[0].meta, /handoff to chief/);
  assert.equal(vm.scratch[0].text, "note <script>");
  assert.equal(vm.scratch[0].meta.includes("<"), false);
});

test("empty and whitespace files are an empty board", () => {
  for (const text of ["", "\n", "   \n\n"]) {
    const parsed = board.parseJsonl(text);
    assert.equal(parsed.empty, true);
    assert.equal(parsed.skipped, 0);
    assert.deepEqual(board.viewModel(parsed), {
      tasks: [],
      decisions: [],
      scratch: [],
      skipped: 0,
      empty: true,
    });
  }
});

test("room selection cannot escape boards/", () => {
  assert.equal(board.roomFromSearch(""), board.DEFAULT_ROOM);
  assert.equal(board.roomFromSearch("?board=other.room_1"), "other.room_1");
  for (const bad of ["../etc/passwd", "/etc/passwd", "http://evil", "..", "a/b", "a\\b", "a..b", ""]) {
    assert.equal(board.safeRoom(bad), board.DEFAULT_ROOM, bad);
  }
  assert.equal(board.safeRoom("board.jsonl"), "board.jsonl");
  assert.equal(board.boardUrl("other.room_1"), "../../boards/other.room_1.jsonl");
  assert.equal(board.boardUrl("../secret"), "../../boards/" + board.DEFAULT_ROOM + ".jsonl");
  assert.equal(board.boardPath(board.DEFAULT_ROOM), "boards/fuse-grok-6f4e970cd8.jsonl");
});

test("missing, empty, and error notes stay non-fatal", () => {
  assert.match(board.noteFor("missing", "fuse-grok-6f4e970cd8"), /Nothing to show/);
  assert.match(board.noteFor("missing", "fuse-grok-6f4e970cd8"), /boards\/fuse-grok-6f4e970cd8\.jsonl/);
  assert.match(board.noteFor("empty"), /empty/);
  assert.match(board.noteFor("error", board.DEFAULT_ROOM, "Failed to fetch"), /Couldn't read the board/);
  assert.match(board.noteFor("ready"), /does not write/);
});

test("render puts record text in textContent and does not build HTML", () => {
  function make(id) {
    const el = {
      id: id,
      className: "",
      textContent: "",
      children: [],
      appendChild(child) { this.children.push(child); return child; },
      removeChild(child) {
        this.children = this.children.filter((item) => item !== child);
      },
      get firstChild() { return this.children[0] || null; },
    };
    Object.defineProperty(el, "innerHTML", {
      set() { throw new Error("innerHTML"); },
    });
    return el;
  }
  const lists = {
    "board-tasks": make("board-tasks"),
    "board-decisions": make("board-decisions"),
    "board-scratch": make("board-scratch"),
  };
  const doc = {
    getElementById(id) { return lists[id] || null; },
    createElement() { return make(); },
  };
  const parsed = board.parseJsonl(
    "{\"type\":\"task\",\"id\":2,\"title\":\"<img src=x onerror=alert(1)>\",\"owner\":\"A\",\"state\":\"blocked\",\"blocked_on\":\"Alex\"}\n" +
    "{\"type\":\"decision\",\"ts\":1790514400,\"decider\":\"Fuse\",\"decision\":\"keep it private\",\"context\":\"<b>no html</b>\"}\n" +
    "{\"type\":\"scratch\",\"ts\":1790514400,\"author\":\"chief\",\"text\":\"scratch <script>\"}\n"
  );
  board.render(doc, parsed);
  const titles = lists["board-tasks"].children[0].children[0].children.map((n) => n.textContent);
  assert.deepEqual(titles, ["<img src=x onerror=alert(1)>", "blocked"]);
  assert.match(lists["board-tasks"].children[0].children[1].textContent, /blocked on Alex/);
  assert.equal(lists["board-decisions"].children[0].children[0].textContent, "keep it private");
  assert.equal(lists["board-decisions"].children[0].children[2].textContent, "<b>no html</b>");
  assert.equal(lists["board-scratch"].children[0].children[0].textContent, "scratch <script>");
});

test("the client source only GETs the board and does not write markup", () => {
  const src = fs.readFileSync(path.join(repoRoot, "docs/muse/board.js"), "utf8");
  assert.match(src, /method:\s*"GET"/);
  assert.doesNotMatch(src, /method:\s*"(POST|PUT|PATCH|DELETE)"/);
  assert.doesNotMatch(src, /localStorage|sessionStorage|indexedDB|innerHTML|outerHTML|insertAdjacentHTML|document\.write/);
  const html = fs.readFileSync(path.join(repoRoot, "docs/muse/index.html"), "utf8");
  const panel = html.slice(html.indexOf('id="board-panel"'), html.indexOf('id="join-panel"'));
  assert.match(panel, /board-tasks/);
  assert.match(panel, /board-decisions/);
  assert.match(panel, /board-scratch/);
  assert.doesNotMatch(panel, /<form|<textarea|<button/i);
});

test("docs/muse and web/muse are the same files", () => {
  const docsDir = path.join(repoRoot, "docs/muse");
  const webDir = path.join(repoRoot, "web/muse");
  const files = fs.readdirSync(docsDir).sort();
  assert.deepEqual(files, fs.readdirSync(webDir).sort());
  for (const file of files) {
    const a = fs.readFileSync(path.join(docsDir, file));
    const b = fs.readFileSync(path.join(webDir, file));
    assert.deepEqual(a, b, file);
  }
});
