const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("fs");
const path = require("path");

const hive = require("../../web/muse/hivemind.js");
const { createHivemind } = require("../../web/muse/hivemindClient.js");
const board = require("../../web/muse/board.js");

const ROOT = path.join(__dirname, "../..");
const HASH = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
const BOARD_DOC = "s1_abd56ffe469abc3e09c830a605b9354e";
const CARD_DOC = "s1_30af8245e25c5c92837eae0f3825691d";
const NOTE_DOC = "s1_907e541e82752e4e0fc7ab34b8163b14";

function queryStub() {
  return {
    equal(attr, value) { return "equal:" + attr + ":" + value; },
    orderAsc(attr) { return "asc:" + attr; },
    limit(n) { return "limit:" + n; },
    offset(n) { return "offset:" + n; },
    search(attr, value) { return "search:" + attr + ":" + value; },
  };
}

function taskData(seq, id, title, state) {
  return {
    boardKey: HASH,
    kind: "task",
    seq: seq,
    cardId: id,
    title: title,
    owner: "Alex",
    state: state || "open",
  };
}

function noteData() {
  return {
    slug: "room-boards",
    title: "Room boards landed",
    summary: "A public note about room boards.",
    tags: ["boards", "tasks"],
    source: "https://example.com/notes",
    authors: ["chief"],
    created: "2026-09-27",
    links: ["classroom-kit-priority"],
    visibility: "public",
    body: "The board file is hashed.\n",
  };
}

function memoryStore(seed) {
  const docs = new Map(seed || []);
  const calls = [];
  return {
    calls: calls,
    docs: docs,
    async listDocuments(params) {
      calls.push(["list", params.collectionId, params.queries.slice(), params.databaseId, params.ttl]);
      const equal = params.queries.find((q) => String(q).startsWith("equal:"));
      const search = params.queries.find((q) => String(q).startsWith("search:"));
      const prefix = params.collectionId + "/";
      let rows = [...docs.entries()].filter(([key]) => key.startsWith(prefix)).map((entry) => entry[1]);
      if (equal) {
        const parts = String(equal).split(":");
        const attr = parts[1];
        const value = parts.slice(2).join(":");
        rows = rows.filter((doc) => doc[attr] === value);
      }
      if (search) {
        const parts = String(search).split(":");
        const attr = parts[1];
        const value = parts.slice(2).join(":");
        rows = rows.filter((doc) => String(doc[attr] || "").includes(value));
      }
      rows.sort((a, b) => (a.seq || 0) - (b.seq || 0));
      const offsetQ = params.queries.find((q) => String(q).startsWith("offset:"));
      const limitQ = params.queries.find((q) => String(q).startsWith("limit:"));
      const offset = offsetQ ? Number(String(offsetQ).slice("offset:".length)) : 0;
      const limit = limitQ ? Number(String(limitQ).slice("limit:".length)) : rows.length;
      return { total: rows.length, documents: rows.slice(offset, offset + limit) };
    },
    async getDocument(params) {
      calls.push(["get", params.collectionId, params.documentId, params.databaseId]);
      const doc = docs.get(params.collectionId + "/" + params.documentId);
      if (!doc) {
        const err = new Error("secret-marker");
        err.code = 404;
        throw err;
      }
      return doc;
    },
    async createDocument(params) {
      calls.push(["create", params.collectionId, params.documentId, params.permissions, params.databaseId]);
      if (docs.has(params.collectionId + "/" + params.documentId)) {
        const err = new Error("secret-marker");
        err.code = 409;
        throw err;
      }
      const saved = Object.assign({ $id: params.documentId, $permissions: params.permissions.slice() }, params.data);
      docs.set(params.collectionId + "/" + params.documentId, saved);
      return saved;
    },
    async updateDocument() {
      throw new Error("update is not allowed");
    },
    async deleteDocument() {
      throw new Error("delete is not allowed");
    },
  };
}

function clientFor(store, logs, extra) {
  return createHivemind(Object.assign({
    databases: store,
    query: queryStub(),
    log(message) { logs.push(message); },
    env: { APPWRITE_DATABASE_ID: "hivemind" },
  }, extra || {}));
}

test("document ids match the seed contract", () => {
  assert.equal(hive.documentId("boards", HASH), BOARD_DOC);
  assert.equal(hive.documentId("cards", HASH + ":1"), CARD_DOC);
  assert.equal(hive.documentId("knowledge", "room-boards"), NOTE_DOC);
  assert.equal(BOARD_DOC.length, 35);
});

test("card documents round-trip to the same board as the jsonl reader", () => {
  const dir = path.join(ROOT, "boards");
  const name = fs.readdirSync(dir).find((file) => file.endsWith(".jsonl"));
  const hash = name.slice(0, -".jsonl".length);
  const text = fs.readFileSync(path.join(dir, name), "utf8");
  const lines = text.split("\n");
  const docs = [];
  for (let i = 0; i < lines.length; i++) {
    if (!lines[i].trim()) continue;
    const data = hive.cardDataFromLine(lines[i], hash, i + 1);
    assert.ok(data);
    docs.push(data);
  }
  assert.deepEqual(hive.cardsToBoard(docs), board.parseBoard(text));
});

test("latest task seq wins and a bad card is skipped", () => {
  const parsed = hive.cardsToBoard([
    taskData(1, 1, "first", "open"),
    { boardKey: HASH, kind: "task", seq: 2, cardId: "nope", title: "bad", owner: "Alex", state: "open" },
    taskData(3, 1, "second", "done"),
    { boardKey: HASH, kind: "scratch", seq: 4, ts: 9, author: "Fuse", text: "pad" },
  ]);
  assert.equal(parsed.tasks.length, 1);
  assert.equal(parsed.tasks[0].title, "second");
  assert.equal(parsed.tasks[0].state, "done");
  assert.equal(parsed.scratch.length, 1);
  assert.equal(parsed.skipped, 1);
});

test("omitted optional nulls match and a different value conflicts", () => {
  const expected = taskData(1, 1, "first", "open");
  const stored = Object.assign({ $id: CARD_DOC, $permissions: [], blockedOn: null }, expected);
  assert.equal(hive.documentsMatch("cards", expected, stored), true);
  stored.title = "other";
  assert.equal(hive.documentsMatch("cards", expected, stored), false);
  stored.title = "first";
  stored.$permissions = ["read(\"any\")"];
  assert.equal(hive.documentsMatch("cards", expected, stored), false);
});

test("readBoard pages cards and does not call git", async () => {
  const store = memoryStore([
    ["cards/" + hive.documentId("cards", HASH + ":1"), taskData(1, 1, "one", "open")],
    ["cards/" + hive.documentId("cards", HASH + ":2"), taskData(2, 2, "two", "claimed")],
  ]);
  const logs = [];
  const client = clientFor(store, logs, { pageSize: 1 });
  const result = await client.readBoard(HASH);
  assert.equal(result.status, "ok");
  assert.equal(result.via, "Appwrite");
  assert.equal(result.board.tasks.length, 2);
  assert.equal(result.board.tasks[1].title, "two");
  const offsets = store.calls.filter((call) => call[0] === "list").map((call) => call[2].find((q) => String(q).startsWith("offset:")));
  assert.deepEqual(offsets, ["offset:0", "offset:1"]);
  assert.equal(logs.length, 0);
  assert.equal(store.calls.some((call) => call[0] === "create"), false);
});

test("an empty Appwrite board is empty and a missing board is empty status", async () => {
  const store = memoryStore([
    ["boards/" + BOARD_DOC, { key: HASH, createdTs: 1, $id: BOARD_DOC, $permissions: [] }],
  ]);
  const logs = [];
  const client = clientFor(store, logs);
  const empty = await client.readBoard(HASH);
  assert.equal(empty.status, "ok");
  assert.equal(empty.board.tasks.length, 0);
  store.docs.clear();
  const missing = await client.readBoard(HASH);
  assert.equal(missing.status, "empty");
  assert.equal(missing.board, null);
});

test("Appwrite read failures log a code and continue", async () => {
  const logs = [];
  const store = memoryStore();
  store.listDocuments = async () => {
    const err = new Error("secret-marker");
    err.code = 500;
    throw err;
  };
  const client = clientFor(store, logs);
  const result = await client.readBoard(HASH);
  assert.equal(result.status, "error");
  assert.equal(result.board, null);
  assert.deepEqual(logs, ["hivemind cards read failed (500)"]);
  assert.equal(logs.join("\n").includes("secret-marker"), false);
  assert.equal(logs.join("\n").includes(HASH), false);
});

test("missing env does not construct a client", async () => {
  const logs = [];
  const client = createHivemind({
    env: {},
    log(message) { logs.push(message); },
  });
  assert.equal(client.isConfigured(), false);
  const result = await client.readBoard(HASH);
  assert.equal(result.status, "unconfigured");
  const note = await client.readKnowledge("room-boards");
  assert.equal(note.status, "unconfigured");
  assert.deepEqual(logs, []);
});

test("a non-https endpoint is not used", async () => {
  const logs = [];
  const client = createHivemind({
    env: {
      APPWRITE_ENDPOINT: "http://127.0.0.1/v1",
      APPWRITE_PROJECT_ID: "fixture-project",
      APPWRITE_API_KEY: "fixture-key",
    },
    log(message) { logs.push(message); },
  });
  assert.equal(client.isConfigured(), false);
  assert.equal((await client.readBoard(HASH)).status, "unconfigured");
  assert.deepEqual(logs, []);
  assert.equal(JSON.stringify(logs).includes("fixture-key"), false);
});

test("knowledge read projects a public note and hides anything else", async () => {
  const note = noteData();
  const id = hive.documentId("knowledge", note.slug);
  const store = memoryStore([
    ["knowledge/" + id, Object.assign({ $id: id, $permissions: [] }, note)],
  ]);
  const logs = [];
  const client = clientFor(store, logs);
  const found = await client.readKnowledge("room-boards");
  assert.equal(found.status, "ok");
  assert.equal(found.note.body, note.body);
  assert.equal(found.note.slug, "room-boards");
  const missing = await client.readKnowledge("missing-note");
  assert.equal(missing.status, "empty");
  store.docs.set("knowledge/" + id, Object.assign({}, store.docs.get("knowledge/" + id), { visibility: "private" }));
  const hidden = await client.readKnowledge("room-boards");
  assert.equal(hidden.status, "empty");
  assert.equal(hidden.note, null);
});

test("knowledge search keeps public hits and soft-fails", async () => {
  const note = noteData();
  const id = hive.documentId("knowledge", note.slug);
  const store = memoryStore([
    ["knowledge/" + id, Object.assign({ $permissions: [] }, note)],
    ["knowledge/other", {
      slug: "other-note",
      title: "Other",
      summary: "Hidden.",
      tags: ["boards"],
      source: "alex",
      authors: ["alex"],
      created: "2026-09-27",
      visibility: "private",
      body: "hashed secret body",
      $permissions: [],
    }],
  ]);
  const logs = [];
  const client = clientFor(store, logs);
  const found = await client.searchKnowledge("hashed");
  assert.equal(found.status, "ok");
  assert.equal(found.notes.length, 1);
  assert.equal(found.notes[0].slug, "room-boards");
  store.listDocuments = async () => {
    const err = new Error("secret-marker");
    err.code = 503;
    throw err;
  };
  const failed = await client.searchKnowledge("hashed");
  assert.equal(failed.status, "error");
  assert.deepEqual(failed.notes, []);
  assert.equal(logs.join("\n").includes("secret-marker"), false);
  assert.match(logs.join("\n"), /knowledge search failed \(503\)/);
});

test("create-or-verify creates, verifies, and does not overwrite", async () => {
  const store = memoryStore();
  const logs = [];
  const client = clientFor(store, logs);
  const card = taskData(1, 1, "first", "open");
  const created = await client.createOrVerifyCard(card);
  assert.equal(created.status, "created");
  assert.equal(created.documentId, CARD_DOC);
  const createCall = store.calls.find((call) => call[0] === "create");
  assert.deepEqual(createCall[3], []);
  assert.equal(createCall[4], "hivemind");
  const again = await client.createOrVerifyCard(card);
  assert.equal(again.status, "verified");
  assert.equal(store.calls.filter((call) => call[0] === "create").length, 1);
  const changed = await client.createOrVerifyCard(taskData(1, 1, "edited", "done"));
  assert.equal(changed.status, "conflict");
  assert.equal(store.calls.filter((call) => call[0] === "create").length, 1);
  assert.equal(store.calls.some((call) => call[0] === "update" || call[0] === "delete"), false);
  assert.match(logs.join("\n"), /conflict/);
  assert.equal(logs.join("\n").includes("edited"), false);
});

test("a 409 race verifies identical data and does not update", async () => {
  const card = taskData(4, 2, "race", "open");
  const id = hive.documentId("cards", HASH + ":4");
  const store = memoryStore([
    ["cards/" + id, Object.assign({ $id: id, $permissions: [] }, card)],
  ]);
  let gets = 0;
  const realGet = store.getDocument.bind(store);
  store.getDocument = async (params) => {
    gets += 1;
    if (gets === 1) {
      const err = new Error("secret-marker");
      err.code = 404;
      throw err;
    }
    return realGet(params);
  };
  store.createDocument = async () => {
    const err = new Error("secret-marker");
    err.code = 409;
    throw err;
  };
  const logs = [];
  const client = clientFor(store, logs);
  const result = await client.createOrVerifyCard(card);
  assert.equal(result.status, "verified");
  assert.equal(result.documentId, id);
  assert.equal(logs.join("\n").includes("secret-marker"), false);
});

test("private or invalid notes are refused before a write", async () => {
  const store = memoryStore();
  const logs = [];
  const client = clientFor(store, logs);
  const note = noteData();
  note.visibility = "private";
  const refused = await client.createOrVerifyNote(note);
  assert.equal(refused.status, "error");
  assert.equal(store.calls.some((call) => call[0] === "create"), false);
  const created = await client.createOrVerifyNote(noteData());
  assert.equal(created.status, "created");
  assert.equal(created.documentId, NOTE_DOC);
  const boardDoc = await client.createOrVerifyBoard({ key: HASH, createdTs: 10 });
  assert.equal(boardDoc.status, "created");
  assert.equal(boardDoc.documentId, BOARD_DOC);
});

test("readBoardOrGit prefers Appwrite and falls back to git/jsonl", async () => {
  const card = taskData(1, 7, "from appwrite", "open");
  const id = hive.documentId("cards", HASH + ":1");
  const store = memoryStore([
    ["cards/" + id, Object.assign({ $permissions: [] }, card)],
  ]);
  const logs = [];
  const urls = [];
  const client = clientFor(store, logs);
  const hit = await client.readBoardOrGit(HASH, {
    pageUrl: "http://127.0.0.1:8080/web/muse/",
    fetchImpl: async (url) => {
      urls.push(String(url));
      return { ok: true, status: 200, headers: { get: () => "text/plain" }, text: async () => "{}\n" };
    },
  });
  assert.equal(hit.origin, "appwrite");
  assert.equal(hit.board.tasks[0].title, "from appwrite");
  assert.equal(urls.length, 0);

  store.listDocuments = async () => {
    const err = new Error("secret-marker");
    err.code = 500;
    throw err;
  };
  const fallback = await client.readBoardOrGit(HASH, {
    pageUrl: "http://127.0.0.1:8080/web/muse/",
    fetchImpl: async () => ({
      ok: true,
      status: 200,
      headers: { get: () => "text/plain" },
      text: async () => "{\"type\":\"scratch\",\"ts\":1,\"author\":\"Fuse\",\"text\":\"git\"}\n",
    }),
  });
  assert.equal(fallback.origin, "git");
  assert.equal(fallback.status, "ok");
  assert.equal(fallback.board.scratch[0].text, "git");
  assert.equal(logs.join("\n").includes("secret-marker"), false);
});

test("tracked client source does not update, delete, or embed a key", () => {
  const src = fs.readFileSync(path.join(ROOT, "web/muse/hivemindClient.js"), "utf8");
  const pure = fs.readFileSync(path.join(ROOT, "web/muse/hivemind.js"), "utf8");
  assert.doesNotMatch(src, /updateDocument|deleteDocument|upsertDocument/);
  assert.doesNotMatch(pure, /updateDocument|deleteDocument/);
  assert.doesNotMatch(src + pure, /standard_|BEGIN [A-Z ]+KEY/);
  const example = fs.readFileSync(path.join(ROOT, ".env.example"), "utf8");
  assert.match(example, /APPWRITE_ENDPOINT=https:\/\/appwrite\.voizel\.com\/v1/);
  assert.match(example, /APPWRITE_PROJECT_ID=6a6da0ae001f1d0582d2/);
  assert.match(example, /APPWRITE_DATABASE_ID=hivemind/);
  assert.match(example, /^APPWRITE_API_KEY=\s*$/m);
  assert.doesNotMatch(example, /APPWRITE_API_KEY=.+/);
});
