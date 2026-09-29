const test = require("node:test");
const assert = require("node:assert/strict");
const crypto = require("crypto");
const fs = require("fs");
const path = require("path");
const { execFileSync } = require("child_process");

const board = require("../../web/muse/board.js");

const ABC = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
const RAW = "https://raw.githubusercontent.com/signalnotnoise/muse-chief-relay/main/boards/" + ABC + ".jsonl";
const ROOT = path.join(__dirname, "../..");

function task(id, title, state) {
  return JSON.stringify({ type: "task", id: id, title: title, owner: "Alex", state: state || "open" });
}

function response(status, body, contentType) {
  return {
    ok: status >= 200 && status < 300,
    status: status,
    headers: {
      get: (name) => (String(name).toLowerCase() === "content-type" ? (contentType || "") : null)
    },
    text: async () => body
  };
}

function recordingFetch(handler) {
  return async (url, opts) => {
    assert.equal(opts && opts.method, "GET");
    assert.equal(opts.cache, "no-store");
    assert.equal(opts.credentials, "omit");
    assert.equal(opts.body, undefined);
    return handler(url);
  };
}

test("boardFileFor is the SHA-256 of the trimmed channel", async () => {
  assert.equal(await board.boardFileFor("abc"), ABC);
  assert.equal(await board.boardFileFor("  abc "), ABC);
});

test("boardFileFor returns null when crypto.subtle is missing", async () => {
  const saved = globalThis.crypto;
  try {
    Object.defineProperty(globalThis, "crypto", { value: {}, configurable: true });
    assert.equal(await board.boardFileFor("abc"), null);
  } finally {
    Object.defineProperty(globalThis, "crypto", { value: saved, configurable: true });
  }
});

test("boardSources rejects a hash that is not 64 lowercase hex characters", () => {
  const page = "http://127.0.0.1:8080/web/muse/";
  assert.throws(() => board.boardSources("abc", page, 0));
  assert.throws(() => board.boardSources(ABC.toUpperCase(), page, 0));
  assert.throws(() => board.boardSources("", page, 0));
  assert.throws(() => board.boardSources(ABC + "00", page, 0));
});

test("boardSources uses same-origin only from the repo root, then GitHub raw", () => {
  const pages = "https://signalnotnoise.github.io/muse-chief-relay/muse/index.html";
  assert.deepEqual(board.boardSources(ABC, pages, 0), [RAW]);

  const local = "http://127.0.0.1:8080/web/muse/";
  assert.deepEqual(board.boardSources(ABC, local, 0), [
    "http://127.0.0.1:8080/boards/" + ABC + ".jsonl",
    RAW
  ]);
  const busted = board.boardSources(ABC, local, 5);
  assert.deepEqual(busted, [
    "http://127.0.0.1:8080/boards/" + ABC + ".jsonl?t=5",
    RAW + "?t=5"
  ]);

  assert.deepEqual(
    board.boardSources(ABC, "file:///tmp/repo/web/muse/index.html", 0),
    [RAW]
  );
});

test("loadBoardText: first hit wins, html is rejected, 404 is missing, a throw is an error", async () => {
  let calls = 0;
  const first = await board.loadBoardText(
    ["http://127.0.0.1/first", "http://127.0.0.1/second"],
    recordingFetch(async (url) => {
      calls += 1;
      assert.equal(url, "http://127.0.0.1/first");
      return response(200, "one\n", "text/plain");
    })
  );
  assert.equal(calls, 1);
  assert.deepEqual(first, { status: "ok", text: "one\n", via: "this repo" });

  const html = await board.loadBoardText(
    ["http://127.0.0.1/page", "https://raw.githubusercontent.com/signalnotnoise/muse-chief-relay/main/boards/x.jsonl"],
    recordingFetch(async (url) => {
      if (String(url).endsWith("/page")) return response(200, "<!doctype html><html></html>", "text/html; charset=utf-8");
      return response(200, "from-raw\n", "text/plain");
    })
  );
  assert.equal(html.status, "ok");
  assert.equal(html.text, "from-raw\n");
  assert.equal(html.via, "GitHub raw on main");

  const markup = await board.loadBoardText(
    ["http://127.0.0.1/markup", "http://127.0.0.1/plain"],
    recordingFetch(async (url) => {
      if (String(url).endsWith("/markup")) return response(200, "<html>not a board</html>", "text/plain");
      return response(200, "plain\n", "application/octet-stream");
    })
  );
  assert.equal(markup.status, "ok");
  assert.equal(markup.text, "plain\n");

  const missing = await board.loadBoardText(
    ["http://127.0.0.1/missing"],
    recordingFetch(async () => response(404, "nope", "text/plain"))
  );
  assert.deepEqual(missing, { status: "missing", text: "", via: "" });

  const thrown = await board.loadBoardText(
    ["http://127.0.0.1/down"],
    recordingFetch(async () => { throw new Error("offline"); })
  );
  assert.equal(thrown.status, "error");
  assert.equal(thrown.text, "");
  assert.equal(thrown.via, "");
});

test("the seeded room board keeps its first lines and records the classroom-kit pick", () => {
  const dir = path.join(ROOT, "boards");
  const files = fs.readdirSync(dir).filter((name) => name.endsWith(".jsonl"));
  assert.equal(files.length, 1);
  assert.match(files[0], /^[0-9a-f]{64}\.jsonl$/);
  const text = fs.readFileSync(path.join(dir, files[0]), "utf8");
  assert.ok(text.endsWith("\n"), "seeded file ends with a newline, so its last line is complete");
  const lines = text.split("\n").slice(0, -1);
  assert.equal(
    lines[0],
    '{"type":"decision","ts":1790514400,"decider":"Fuse","decision":"Room board created","context":"Task board + decision log + scratch pad shared by the room\'s human and agents (Fuse + chief). Commit like CHANGELOG."}'
  );
  assert.equal(
    lines[1],
    '{"type":"task","id":1,"title":"teaching-kit card — Alex picks the workload","owner":"Alex","state":"open"}'
  );
  const parsed = board.parseBoard(text);
  assert.equal(parsed.skipped, 0);
  assert.equal(parsed.scratch.length, 0);
  assert.equal(parsed.decisions[0].decider, "Fuse");
  assert.equal(parsed.decisions[0].decision, "Room board created");
  assert.equal(parsed.decisions[1].decider, "Alex");
  assert.equal(parsed.decisions[1].decision, "task 1 closed");
  assert.equal(parsed.decisions[2].decider, "Alex");
  assert.equal(parsed.decisions[2].decision, "Classroom / lesson-coach kit is build priority #1");
  assert.equal(
    parsed.decisions[2].context,
    "Closest to shipped bridge+boards+knowledge. Medical parked. 3D asset-QA is promo angle, not first product build. First card: Lesson outline coach (SME-gate checklist on teacher outlines → knowledge/ + board tasks)."
  );
  assert.equal(parsed.decisions[3].decider, "chief");
  assert.equal(parsed.decisions[3].decision, "First teaching-kit card is Lesson outline coach");
  assert.equal(parsed.decisions[3].context, "Alex authorized chief to pick the closest card.");
  assert.equal(parsed.decisions[2].ts, parsed.decisions[1].ts + 1);
  assert.equal(parsed.decisions[3].ts, parsed.decisions[2].ts + 1);
  assert.equal(parsed.decisions.length, 5);
  assert.equal(parsed.decisions[4].decider, "Alex");
  assert.equal(
    parsed.decisions[4].decision,
    "Muse product backlog cards for workspaces, file/image send, and LaTeX files should be seeded on the room board"
  );
  assert.equal(
    parsed.decisions[4].context,
    "Alex listed them in chat after asking to start adding things to the board."
  );
  assert.equal(parsed.decisions[4].ts, 1790676963);
  assert.equal(lines.length, 14);
  assert.equal(
    lines[9],
    '{"type":"task","id":5,"title":"3D asset-QA bot — SocialMgr promo clips only for now","owner":"SocialMgr","state":"open"}'
  );
  assert.equal(parsed.tasks.length, 8);
  const byId = new Map(parsed.tasks.map((item) => [item.id, item]));
  assert.equal(byId.get(1).owner, "Alex");
  assert.equal(byId.get(1).state, "done");
  assert.equal(byId.get(1).title, "teaching-kit card — Alex picked the classroom / lesson-coach kit");
  assert.equal(byId.get(2).owner, "chief");
  assert.equal(byId.get(2).state, "claimed");
  assert.equal(byId.get(2).title, "Lesson outline coach — SME-gate checklist + outline critique path");
  assert.equal(byId.get(3).owner, "chief");
  assert.equal(byId.get(3).state, "done");
  assert.equal(byId.get(3).title, "Persist morning OSS brainstorm into knowledge note");
  assert.equal(byId.get(4).owner, "Alex");
  assert.equal(byId.get(4).state, "blocked");
  assert.equal(byId.get(4).blocked_on, "privacy design");
  assert.equal(byId.get(4).title, "Park medical track until privacy story");
  assert.equal(byId.get(5).owner, "SocialMgr");
  assert.equal(byId.get(5).state, "open");
  assert.equal(byId.get(5).title, "3D asset-QA bot — SocialMgr promo clips only for now");
  assert.equal(byId.get(6).owner, "Alex");
  assert.equal(byId.get(6).state, "open");
  assert.equal(byId.get(6).title, "Workspaces — shared workspaces in Muse");
  assert.equal(byId.get(7).owner, "Alex");
  assert.equal(byId.get(7).state, "open");
  assert.equal(byId.get(7).title, "File and image send — upload/send files and images (app path, not hack.chat attachments)");
  assert.equal(byId.get(8).owner, "Alex");
  assert.equal(byId.get(8).state, "open");
  assert.equal(byId.get(8).title, "LaTeX files — support latex documents in Muse");
});

test("an unterminated last line is kept when it is a valid record and ignored when it is not", () => {
  const broken = task(1, "complete card", "open") + "\n" + "{\"type\":\"task\",\"id\":1,\"title\":\"still being";
  assert.ok(!broken.endsWith("\n"));
  const ignored = board.parseBoard(broken);
  assert.equal(ignored.tasks.length, 1);
  assert.equal(ignored.tasks[0].title, "complete card");
  assert.equal(ignored.tasks[0].state, "open");
  assert.equal(ignored.skipped, 0);

  const kept = board.parseBoard(task(1, "complete card", "open") + "\n" + task(1, "finished without a newline", "claimed"));
  assert.equal(kept.tasks.length, 1);
  assert.equal(kept.tasks[0].title, "finished without a newline");
  assert.equal(kept.tasks[0].state, "claimed");
  assert.equal(kept.skipped, 0);
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

test("an empty file yields no records, and a valid record without a trailing newline is kept", () => {
  assert.deepEqual(board.parseBoard(""), { tasks: [], decisions: [], scratch: [], skipped: 0 });
  const parsed = board.parseBoard("{\"type\":\"scratch\",\"ts\":1,\"author\":\"a\",\"text\":\"x\"}");
  assert.equal(parsed.skipped, 0);
  assert.equal(parsed.scratch.length, 1);
  assert.equal(parsed.scratch[0].text, "x");
  assert.deepEqual(board.parseBoard("{\"type\":\"scratch\",\"ts\":1"), {
    tasks: [], decisions: [], scratch: [], skipped: 0
  });
});

test("the client does not default a channel, store one, or write the board", () => {
  const page = [
    fs.readFileSync(path.join(ROOT, "web/muse/src/App.vue"), "utf8"),
    fs.readFileSync(path.join(ROOT, "web/muse/src/RoomBoard.vue"), "utf8"),
    fs.readFileSync(path.join(ROOT, "web/muse/src/roomBoard.js"), "utf8"),
  ].join("\n");
  const channel = page.match(/<input\b[^>]*\bid="channel"[^>]*>/)[0];
  assert.match(channel, /placeholder="your-channel-name"/);
  assert.doesNotMatch(channel, /\svalue=/);
  assert.match(page, /id="room-board"/);
  assert.match(page, /Join a channel to see its room board\./);
  assert.match(page, /id="board-reload"[^>]*disabled/);
  assert.match(page, /id="board-body"[^>]*aria-live="polite"/);
  assert.doesNotMatch(page, /aria-readonly/);
  assert.doesNotMatch(page, /id="board-file"/);
  const src = fs.readFileSync(path.join(ROOT, "web/muse/board.js"), "utf8");
  assert.doesNotMatch(src, /localStorage|sessionStorage|method:\s*["'](PUT|POST|PATCH)|writeFile|appendFile/);
  assert.match(src, /method:\s*"GET"/);
  assert.equal(board.BOARD_FILE, undefined);
  assert.equal(board.RAW_URL, undefined);

  const app = fs.readFileSync(path.join(ROOT, "web/muse/src/useChat.js"), "utf8");
  const connectBody = app.slice(app.indexOf("function connect("), app.indexOf("function reconnectNowIfNeeded"));
  assert.match(connectBody, /startBoard\(channel\)/);
  const openBody = app.slice(app.indexOf("function openSocket("), app.indexOf("function connect("));
  assert.doesNotMatch(openBody, /startBoard|loadBoardText|boardFileFor/);
  const disconnectBody = app.slice(app.indexOf("function disconnect("), app.indexOf("function onSend("));
  assert.match(disconnectBody, /showBoardPlaceholder\(\)/);
  assert.match(page, /No board for this channel yet\./);
  assert.match(page, /Room board needs HTTPS or localhost\./);
  const boardPart = page.slice(page.indexOf("Room board."));
  assert.ok(boardPart.length > 40);
  assert.doesNotMatch(boardPart, /localStorage|sessionStorage|console\.|document\.title/);
});

test("board code uses no innerHTML", () => {
  const src = fs.readFileSync(path.join(ROOT, "web/muse/board.js"), "utf8");
  assert.doesNotMatch(src, /innerHTML/);
  const vue = fs.readFileSync(path.join(ROOT, "web/muse/src/RoomBoard.vue"), "utf8");
  const logic = fs.readFileSync(path.join(ROOT, "web/muse/src/roomBoard.js"), "utf8");
  const boardPart = logic.slice(logic.indexOf("Room board."));
  assert.ok(boardPart.length > 40);
  assert.doesNotMatch(vue, /innerHTML|v-html/);
  assert.doesNotMatch(logic, /innerHTML|v-html/);
  assert.doesNotMatch(boardPart, /innerHTML|v-html/);
});

test("no tracked text token hashes to a board filename", () => {
  const names = fs.readdirSync(path.join(ROOT, "boards")).filter((name) => name.endsWith(".jsonl"));
  assert.ok(names.length > 0);
  const hashes = names.map((name) => name.slice(0, -".jsonl".length));
  const listed = execFileSync(
    "git",
    ["ls-files", "-z", "--", "web", "docs", "boards", "tests", "agents", "knowledge", "README.md", "CHANGELOG.md"],
    { cwd: ROOT, encoding: "utf8" }
  );
  const tokens = new Set();
  for (const rel of listed.split("\0")) {
    if (!rel) continue;
    const abs = path.join(ROOT, rel);
    const buf = fs.readFileSync(abs);
    if (buf.includes(0)) continue;
    const re = /[A-Za-z0-9._-]+/g;
    let match;
    const text = buf.toString("utf8");
    while ((match = re.exec(text))) tokens.add(match[0]);
  }
  // No exceptions. The watch view's channel is VITE_WATCH_CHANNEL at build
  // time and is not committed.
  for (const token of tokens) {
    const dig = crypto.createHash("sha256").update(token).digest("hex");
    assert.ok(!hashes.includes(dig), "a tracked token hashes to a board filename");
  }
});

test("docs/muse is the Vite build of the Vue client, and board.js is the shared reader", () => {
  const docsDir = path.join(ROOT, "docs/muse");
  const index = fs.readFileSync(path.join(docsDir, "index.html"), "utf8");
  assert.match(index, /id="app"/);
  assert.match(index, /src="\.\/assets\//);
  assert.match(index, /href="\.\/assets\//);
  assert.doesNotMatch(index, /src="\/assets\//);
  assert.doesNotMatch(index, /href="\/assets\//);
  assert.equal(fs.existsSync(path.join(docsDir, "app.js")), false);
  assert.equal(fs.existsSync(path.join(docsDir, "board.js")), false);
  assert.equal(fs.existsSync(path.join(docsDir, "styles.css")), false);
  assert.equal(fs.existsSync(path.join(docsDir, "reconnect.js")), false);

  let published = "";
  const walk = (dir) => {
    for (const name of fs.readdirSync(dir)) {
      const p = path.join(dir, name);
      if (fs.statSync(p).isDirectory()) walk(p);
      else if (/\.(html|js|css)$/.test(name)) published += fs.readFileSync(p, "utf8");
    }
  };
  walk(docsDir);
  assert.match(published, /room-board/);
  assert.match(published, /board-reload/);
  assert.match(published, /Join a channel to see its room board\./);
  assert.match(published, /No board for this channel yet\./);
  assert.match(published, /Room board needs HTTPS or localhost\./);
  assert.doesNotMatch(published, /v-html/);
  assert.doesNotMatch(published, /localStorage|sessionStorage/);
  assert.equal(typeof board.parseBoard, "function");
  assert.equal(typeof board.loadBoardText, "function");
});
