const test = require("node:test");
const assert = require("node:assert/strict");
const { spawn } = require("node:child_process");
const fs = require("fs");
const path = require("path");

const hive = require("../../web/muse/hivemind.js");
const { createHivemind } = require("../../web/muse/hivemindClient.js");
const { createMessageMirror, noteAcceptedChat, mirrorEnabled } = require("../../web/muse/messageMirror.js");

const ROOT = path.join(__dirname, "../..");
const CHANNEL = "channel-fixture";
const UUID = "11111111-1111-4111-8111-111111111111";
const KEY = hive.workspaceKeyFromChannel(CHANNEL);

function queryStub() {
  return {
    equal(attr, value) { return "equal:" + attr + ":" + value; },
    orderAsc(attr) { return "asc:" + attr; },
    limit(n) { return "limit:" + n; },
    offset(n) { return "offset:" + n; },
    search(attr, value) { return "search:" + attr + ":" + value; },
  };
}

function memoryStore(seed) {
  const docs = new Map(seed || []);
  const calls = [];
  return {
    calls: calls,
    docs: docs,
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
      calls.push(["create", params.collectionId, params.documentId, params.data, params.permissions, params.databaseId]);
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
    async listDocuments() {
      throw new Error("list is not used by the mirror");
    },
  };
}

function clientFor(store, logs) {
  return createHivemind({
    databases: store,
    query: queryStub(),
    log(message) { logs.push(message); },
    env: { APPWRITE_DATABASE_ID: "hivemind" },
  });
}

function chat(extra) {
  return Object.assign({
    id: UUID,
    channel: CHANNEL,
    sender: "Ada",
    text: "hello",
    ts: 1700000000,
    trip: "AbCdEf",
    password: "join-secret",
    pass: "nope",
    token: "session-token",
  }, extra || {});
}

test("workspace key is sha256 of the channel and never the channel", () => {
  assert.equal(KEY, hive.workspaceKeyFromChannel("  " + CHANNEL + "  "));
  assert.equal(KEY.length, 64);
  assert.notEqual(KEY, CHANNEL);
  assert.equal(KEY.includes(CHANNEL), false);
  assert.equal(hive.workspaceKeyFromChannel("abc"), "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
  const plan = hive.planRoomMirror(chat());
  assert.equal(plan.ok, true);
  assert.equal(plan.workspace.key, KEY);
  assert.equal(plan.message.workspaceKey, KEY);
  assert.equal(plan.documentId, UUID);
  assert.equal(plan.message.sender, "Ada");
  assert.equal(plan.message.threadKey, "room");
  assert.equal(plan.message.ts, 1700000000);
  assert.equal(plan.workspace.createdTs, 1700000000);
  const packed = JSON.stringify(plan);
  assert.equal(packed.includes(CHANNEL), false);
  assert.equal(packed.includes("AbCdEf"), false);
  assert.equal(packed.includes("join-secret"), false);
  assert.equal(packed.includes("session-token"), false);
  assert.equal(Object.hasOwn(plan.workspace, "name"), false);
  assert.equal(Object.hasOwn(plan.workspace, "description"), false);
  const slugNamedAsChannel = hive.planRoomMirror({
    id: UUID,
    channel: "room-slug",
    workspaceKey: "room-slug",
    sender: "Ada",
    text: "hello",
    ts: 10,
  });
  assert.equal(slugNamedAsChannel.ok, false);
  assert.equal(slugNamedAsChannel.reason, "key");
});

test("message document id is the room uuid and oversize text is refused", async () => {
  const store = memoryStore();
  const logs = [];
  const client = clientFor(store, logs);
  const plan = hive.planRoomMirror(chat());
  const created = await client.createOrVerifyMessage(plan.message, plan.documentId);
  assert.equal(created.status, "created");
  assert.equal(created.documentId, UUID);
  const messageCreate = store.calls.find((call) => call[0] === "create" && call[1] === "messages");
  assert.equal(messageCreate[2], UUID);
  assert.equal(messageCreate[3].text, "hello");
  assert.equal(messageCreate[3].workspaceKey, KEY);
  assert.deepEqual(messageCreate[4], []);
  assert.equal(JSON.stringify(messageCreate[3]).includes(CHANNEL), false);

  const workspace = await client.createOrVerifyWorkspace(plan.workspace);
  assert.equal(workspace.status, "created");
  assert.match(workspace.documentId, /^s1_[a-f0-9]{32}$/);
  assert.notEqual(workspace.documentId, KEY);
  assert.notEqual(workspace.documentId, CHANNEL);

  const exact = hive.planRoomMirror(chat({ text: "y".repeat(hive.TEXT_MAX) }));
  assert.equal(exact.ok, true);
  const over = hive.planRoomMirror(chat({ text: "y".repeat(hive.TEXT_MAX + 1) }));
  assert.equal(over.ok, false);
  assert.equal(over.reason, "length");
  const mirrorLogs = [];
  const calls = [];
  const mirror = createMessageMirror({
    env: { HIVEMIND_MESSAGE_MIRROR: "1" },
    hivemind: {
      async createOrVerifyWorkspace() { calls.push("workspace"); return { status: "created" }; },
      async createOrVerifyMessage() { calls.push("message"); return { status: "created" }; },
    },
    log(message) { mirrorLogs.push(message); },
    retryDelays: [],
  });
  const refused = await mirror.observeAcceptedChat(chat({ text: "z".repeat(8193) }));
  assert.equal(refused.status, "error");
  assert.deepEqual(calls, []);
  assert.match(mirrorLogs.join("\n"), /refused \(length\)/);
  assert.equal(mirrorLogs.join("\n").includes("z".repeat(20)), false);
});

test("a 409 on the message uuid is dedup, not an update", async () => {
  const plan = hive.planRoomMirror(chat());
  const store = memoryStore([
    ["messages/" + UUID, Object.assign({ $id: UUID, $permissions: [] }, plan.message)],
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
  const raced = await client.createOrVerifyMessage(Object.assign({}, plan.message, { text: "edited" }), UUID.toUpperCase());
  assert.equal(raced.status, "verified");
  assert.equal(raced.documentId, UUID);
  assert.equal(store.docs.get("messages/" + UUID).text, "hello");
  assert.equal(logs.join("\n").includes("secret-marker"), false);
  assert.equal(logs.join("\n").includes("edited"), false);
  assert.equal(store.calls.some((call) => call[0] === "update" || call[0] === "delete"), false);
});

test("flag off does nothing even when Appwrite env is present", async () => {
  for (const flag of [undefined, "", "0", "false", "off"]) {
    const env = {
      APPWRITE_ENDPOINT: "https://appwrite.example/v1",
      APPWRITE_PROJECT_ID: "fixture-project",
      APPWRITE_DATABASE_ID: "hivemind",
      APPWRITE_API_KEY: "secret-marker",
    };
    if (flag !== undefined) env.HIVEMIND_MESSAGE_MIRROR = flag;
    assert.equal(mirrorEnabled(env), false);
    const calls = [];
    const logs = [];
    const mirror = createMessageMirror({
      env: env,
      hivemind: {
        async createOrVerifyWorkspace() { calls.push("workspace"); throw new Error("secret-marker"); },
        async createOrVerifyMessage() { calls.push("message"); throw new Error("secret-marker"); },
      },
      log(message) { logs.push(message); },
    });
    const result = await mirror.observeAcceptedChat(chat());
    assert.equal(result.status, "disabled");
    assert.deepEqual(calls, []);
    assert.deepEqual(logs, []);
  }
});

test("mirror errors do not throw into the chat path and are retried", async () => {
  const logs = [];
  let workspaceCalls = 0;
  const mirror = createMessageMirror({
    env: { HIVEMIND_MESSAGE_MIRROR: "1", APPWRITE_API_KEY: "secret-marker" },
    hivemind: {
      async createOrVerifyWorkspace() {
        workspaceCalls += 1;
        if (workspaceCalls === 1) {
          const err = new Error("secret-marker");
          err.code = 503;
          throw err;
        }
        return { status: "created" };
      },
      async createOrVerifyMessage(data, id) {
        assert.equal(id, UUID);
        assert.equal(data.workspaceKey, KEY);
        assert.equal(JSON.stringify(data).includes(CHANNEL), false);
        return { status: "created", documentId: id };
      },
    },
    log(message) { logs.push(message); },
    retryDelays: [0],
    sleep() { return Promise.resolve(); },
  });
  let threw = false;
  let result;
  try {
    noteAcceptedChat({ observeAcceptedChat() { throw new Error("secret-marker"); } }, chat());
    result = await mirror.observeAcceptedChat(chat());
  } catch (e) {
    threw = true;
  }
  assert.equal(threw, false);
  assert.equal(result.status, "created");
  assert.equal(workspaceCalls, 2);
  assert.match(logs.join("\n"), /workspaces create-or-verify failed \(503\)/);
  assert.equal(logs.join("\n").includes("secret-marker"), false);
  assert.equal(logs.join("\n").includes(CHANNEL), false);
});

test("a full mirror queue does not throw and does not write", async () => {
  let release;
  const gate = new Promise((resolve) => { release = resolve; });
  const logs = [];
  let writes = 0;
  const mirror = createMessageMirror({
    env: { HIVEMIND_MESSAGE_MIRROR: "1" },
    hivemind: {
      async createOrVerifyWorkspace() {
        writes += 1;
        await gate;
        return { status: "verified" };
      },
      async createOrVerifyMessage(_data, id) {
        return { status: "verified", documentId: id };
      },
    },
    log(message) { logs.push(message); },
    maxPending: 1,
    retryDelays: [],
  });
  const first = mirror.observeAcceptedChat(chat());
  await new Promise((resolve) => setImmediate(resolve));
  const second = await mirror.observeAcceptedChat(chat({ id: "22222222-2222-4222-8222-222222222222" }));
  assert.equal(second.status, "error");
  assert.match(logs.join("\n"), /refused \(queue\)/);
  release();
  const done = await first;
  assert.equal(done.status, "verified");
  assert.equal(writes, 1);
});

test("the helper exits without writing when the flag is off", async () => {
  const script = path.join(ROOT, "web/muse/messageMirror.js");
  const child = spawn(process.execPath, [script], {
    env: Object.assign({}, process.env, {
      HIVEMIND_MESSAGE_MIRROR: "0",
      APPWRITE_API_KEY: "secret-marker",
      APPWRITE_ENDPOINT: "https://appwrite.example/v1",
      APPWRITE_PROJECT_ID: "fixture-project",
    }),
  });
  let stderr = "";
  child.stderr.on("data", (chunk) => { stderr += chunk; });
  child.stdin.end(JSON.stringify(chat()));
  const code = await new Promise((resolve) => child.on("close", resolve));
  assert.equal(code, 0);
  assert.equal(stderr.includes("secret-marker"), false);
  assert.equal(stderr.includes(CHANNEL), false);
  assert.equal(stderr.trim(), "");
});

test("docs and the example env keep the mirror off", () => {
  const example = fs.readFileSync(path.join(ROOT, ".env.example"), "utf8");
  const docs = fs.readFileSync(path.join(ROOT, "docs/hivemind.md"), "utf8");
  const changelog = fs.readFileSync(path.join(ROOT, "CHANGELOG.md"), "utf8");
  assert.match(example, /^HIVEMIND_MESSAGE_MIRROR=0\s*$/m);
  assert.match(docs, /HIVEMIND_MESSAGE_MIRROR/);
  assert.match(docs, /off by default|default is off|Default is off|unless `HIVEMIND_MESSAGE_MIRROR` is `1`/);
  assert.match(changelog, /HIVEMIND_MESSAGE_MIRROR/);
  const src = fs.readFileSync(path.join(ROOT, "web/muse/messageMirror.js"), "utf8")
    + fs.readFileSync(path.join(ROOT, "web/muse/hivemindClient.js"), "utf8");
  assert.doesNotMatch(src, /updateDocument|deleteDocument/);
  assert.doesNotMatch(src, /standard_|BEGIN [A-Z ]+KEY/);
});
