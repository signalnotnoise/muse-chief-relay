const test = require("node:test");
const assert = require("node:assert/strict");

let roomBoard;

test.before(async () => {
  roomBoard = await import("../../web/muse/src/roomBoard.js");
});

const HASH = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

function response(status, body, contentType) {
  return {
    ok: status >= 200 && status < 300,
    status,
    headers: {
      get: (name) => (String(name).toLowerCase() === "content-type" ? (contentType || "") : null),
    },
    text: async () => body,
  };
}

function jsonl() {
  return [
    JSON.stringify({ type: "task", id: 1, title: "teaching card", owner: "Alex", state: "open" }),
    JSON.stringify({ type: "decision", ts: 10, decider: "Fuse", decision: "Room board created", context: "seed" }),
    JSON.stringify({ type: "scratch", ts: 11, author: "Fuse", text: "a note" }),
    "",
  ].join("\n");
}

test("nothing is fetched until Connect, and Reload stays disabled", async () => {
  const urls = [];
  const room = roomBoard.createRoomBoard({
    pageUrl: () => "http://127.0.0.1:8080/docs/muse/",
    fetchImpl: async (url) => {
      urls.push(String(url));
      return response(404, "nope", "text/plain");
    },
  });
  const initial = room.getState();
  assert.equal(initial.message, "Join a channel to see its room board.");
  assert.equal(initial.reloadDisabled, true);
  assert.equal(initial.board, null);
  assert.equal(initial.statusText, "");
  await room.reloadBoard();
  assert.equal(urls.length, 0);
});

test("a missing board is a 404 message and chat-side callers are not rejected", async () => {
  const room = roomBoard.createRoomBoard({
    pageUrl: () => "https://signalnotnoise.github.io/muse-chief-relay/muse/index.html",
    fetchImpl: async () => response(404, "nope", "text/plain"),
  });
  const state = await room.startBoard("abc");
  assert.equal(state.status, undefined);
  assert.equal(state.message, "No board for this channel yet.");
  assert.equal(state.statusText, "no board");
  assert.equal(state.reloadDisabled, false);
  assert.equal(state.board, null);
  assert.equal(JSON.stringify(state).includes("abc"), false);
  assert.equal(JSON.stringify(state).includes(HASH), false);
});

test("without a hash the panel asks for HTTPS or localhost and does not fetch", async () => {
  let fetched = false;
  const room = roomBoard.createRoomBoard({
    board: {
      async boardFileFor() { return null; },
      boardSources() { fetched = true; return []; },
      async loadBoardText() { fetched = true; return { status: "error", text: "", via: "" }; },
      parseBoard() { fetched = true; return null; },
    },
  });
  const state = await room.startBoard("abc");
  assert.equal(state.message, "Room board needs HTTPS or localhost.");
  assert.equal(state.statusText, "unavailable");
  assert.equal(state.statusKind, "err");
  assert.equal(state.reloadDisabled, true);
  assert.equal(fetched, false);
});

test("a thrown fetch is an error state and does not reject", async () => {
  const room = roomBoard.createRoomBoard({
    pageUrl: () => "https://signalnotnoise.github.io/muse-chief-relay/muse/",
    fetchImpl: async () => { throw new Error("offline"); },
  });
  const state = await room.startBoard("abc");
  assert.equal(state.message, "Couldn't read the room board.");
  assert.equal(state.statusKind, "err");
  assert.equal(state.board, null);
});

test("a loaded board is parsed, Reload refetches, and Disconnect clears it", async () => {
  const urls = [];
  const room = roomBoard.createRoomBoard({
    pageUrl: () => "http://127.0.0.1:8080/docs/muse/index.html",
    now: () => 42,
    fetchImpl: async (url) => {
      urls.push(String(url));
      return response(200, jsonl(), "text/plain");
    },
  });
  const channel = "zz-board-channel";
  const state = await room.startBoard(channel);
  assert.equal(state.statusText, "read-only");
  assert.equal(state.statusKind, "ok");
  assert.equal(state.reloadDisabled, false);
  assert.equal(state.board.tasks.length, 1);
  assert.equal(state.board.tasks[0].title, "teaching card");
  assert.equal(state.board.decisions[0].decision, "Room board created");
  assert.equal(state.board.scratch[0].text, "a note");
  assert.match(state.viaText, /this repo/);
  assert.equal(JSON.stringify(state).includes(channel), false);
  assert.equal(urls.some((url) => url.includes(channel)), false);
  assert.equal(urls.some((url) => url.includes("/boards/") && url.endsWith(".jsonl")), true);

  const beforeReload = urls.length;
  await room.reloadBoard();
  assert.ok(urls.length > beforeReload);
  assert.ok(urls.some((url) => url.includes("t=42")));

  const cleared = room.showBoardPlaceholder();
  assert.equal(cleared.message, "Join a channel to see its room board.");
  assert.equal(cleared.board, null);
  assert.equal(cleared.reloadDisabled, true);
  assert.equal(cleared.viaText, "");
  assert.equal(cleared.statusText, "");
  const afterClear = urls.length;
  await room.reloadBoard();
  assert.equal(urls.length, afterClear);
});

test("Appwrite is preferred, and a miss or a throw falls back to git", async () => {
  const urls = [];
  const hiveBoard = {
    tasks: [{ type: "task", id: 9, title: "from appwrite", owner: "Alex", state: "open" }],
    decisions: [],
    scratch: [],
    skipped: 0,
  };
  const preferred = roomBoard.createRoomBoard({
    pageUrl: () => "http://127.0.0.1:8080/docs/muse/",
    hivemind: {
      async readBoard(hash) {
        assert.equal(hash, HASH);
        return { status: "ok", board: hiveBoard, via: "Appwrite" };
      },
    },
    fetchImpl: async (url) => {
      urls.push(String(url));
      return response(200, jsonl(), "text/plain");
    },
  });
  const state = await preferred.startBoard("abc");
  assert.equal(urls.length, 0);
  assert.equal(state.board.tasks[0].title, "from appwrite");
  assert.match(state.viaText, /Appwrite/);
  assert.equal(state.statusText, "read-only");
  assert.equal(JSON.stringify(state).includes("abc"), false);

  const thrown = roomBoard.createRoomBoard({
    pageUrl: () => "http://127.0.0.1:8080/docs/muse/",
    hivemind: {
      async readBoard() { throw new Error("offline"); },
    },
    fetchImpl: async (url) => {
      urls.push(String(url));
      return response(200, jsonl(), "text/plain");
    },
  });
  const fellBack = await thrown.startBoard("abc");
  assert.equal(fellBack.board.tasks[0].title, "teaching card");
  assert.match(fellBack.viaText, /this repo/);
  assert.equal(urls.length > 0, true);

  const before = urls.length;
  const empty = roomBoard.createRoomBoard({
    pageUrl: () => "http://127.0.0.1:8080/docs/muse/",
    hivemind: {
      async readBoard() { return { status: "empty", board: null, via: "" }; },
    },
    fetchImpl: async (url) => {
      urls.push(String(url));
      return response(404, "nope", "text/plain");
    },
  });
  const missing = await empty.startBoard("abc");
  assert.equal(missing.message, "No board for this channel yet.");
  assert.ok(urls.length > before);
});

test("a non-https Appwrite env falls back and does not fetch that endpoint", async () => {
  const prev = {
    APPWRITE_ENDPOINT: process.env.APPWRITE_ENDPOINT,
    APPWRITE_PROJECT_ID: process.env.APPWRITE_PROJECT_ID,
    APPWRITE_API_KEY: process.env.APPWRITE_API_KEY,
  };
  process.env.APPWRITE_ENDPOINT = "http://127.0.0.1/v1";
  process.env.APPWRITE_PROJECT_ID = "fixture-project";
  process.env.APPWRITE_API_KEY = "fixture-key";
  const urls = [];
  try {
    const room = roomBoard.createRoomBoard({
      pageUrl: () => "http://127.0.0.1:8080/docs/muse/index.html",
      fetchImpl: async (url) => {
        urls.push(String(url));
        return response(200, jsonl(), "text/plain");
      },
    });
    const state = await room.startBoard("abc");
    assert.equal(state.board.tasks[0].title, "teaching card");
    assert.match(state.viaText, /this repo/);
    assert.equal(urls.some((url) => url.includes("fixture-key")), false);
    assert.equal(urls.some((url) => url.startsWith("http://127.0.0.1/v1")), false);
    assert.equal(JSON.stringify(state).includes("fixture-key"), false);
  } finally {
    for (const key of Object.keys(prev)) {
      if (prev[key] === undefined) delete process.env[key];
      else process.env[key] = prev[key];
    }
  }
});

test("an Appwrite load that finishes after Disconnect does not replace the placeholder", async () => {
  let releaseLoad;
  let markEntered;
  const entered = new Promise((resolve) => { markEntered = resolve; });
  const room = roomBoard.createRoomBoard({
    hivemind: {
      readBoard() {
        markEntered();
        return new Promise((resolve) => { releaseLoad = resolve; });
      },
    },
    fetchImpl: async () => response(200, jsonl(), "text/plain"),
  });
  const pending = room.startBoard("abc");
  await entered;
  room.showBoardPlaceholder();
  releaseLoad({
    status: "ok",
    via: "Appwrite",
    board: { tasks: [{ id: 1, title: "late", owner: "Alex", state: "open" }], decisions: [], scratch: [], skipped: 0 },
  });
  await pending;
  const state = room.getState();
  assert.equal(state.message, "Join a channel to see its room board.");
  assert.equal(state.board, null);
  assert.equal(state.reloadDisabled, true);
});

test("a load that finishes after Disconnect does not replace the placeholder", async () => {
  let releaseLoad;
  let markEntered;
  const entered = new Promise((resolve) => { markEntered = resolve; });
  const room = roomBoard.createRoomBoard({
    board: {
      boardFileFor() { return HASH; },
      boardSources() { return ["http://127.0.0.1/board.jsonl"]; },
      loadBoardText() {
        markEntered();
        return new Promise((resolve) => { releaseLoad = resolve; });
      },
      parseBoard() {
        return { tasks: [{ id: 1, title: "late", owner: "Alex", state: "open" }], decisions: [], scratch: [], skipped: 0 };
      },
    },
  });
  const pending = room.startBoard("abc");
  await entered;
  room.showBoardPlaceholder();
  releaseLoad({ status: "ok", text: "{}\n", via: "this repo" });
  await pending;
  const state = room.getState();
  assert.equal(state.message, "Join a channel to see its room board.");
  assert.equal(state.board, null);
  assert.equal(state.reloadDisabled, true);
  assert.equal(state.viaText, "");
});
