const test = require("node:test");
const assert = require("node:assert/strict");

test("relay URL defaults to the local relay and rejects unsafe values", async () => {
  const { LOCAL_RELAY_URL, resolveRelayUrl } = await import("../../web/muse/src/relayUrl.js");
  assert.equal(LOCAL_RELAY_URL, "ws://127.0.0.1:8787/relay");
  assert.equal(resolveRelayUrl(undefined), LOCAL_RELAY_URL);
  assert.equal(resolveRelayUrl(""), LOCAL_RELAY_URL);
  assert.equal(resolveRelayUrl("   "), LOCAL_RELAY_URL);
  assert.equal(resolveRelayUrl("wss://example.test/relay"), "wss://example.test/relay");
  assert.equal(resolveRelayUrl("  wss://example.test/relay  "), "wss://example.test/relay");
  assert.equal(resolveRelayUrl("http://example.test/relay"), "");
  assert.equal(resolveRelayUrl("wss://user:secret@example.test/relay"), "");
  assert.equal(resolveRelayUrl("wss://example.test/relay?token=secret"), "");
  assert.equal(resolveRelayUrl("wss://example.test/relay#frag"), "");
  assert.equal(resolveRelayUrl("not a url"), "");
});

test("join sends a public trip and never a nick#password", async () => {
  const { PROTOCOL_NAME, chatFrame, isHello, joinFrame, publicTrip } = await import(
    "../../web/muse/src/relayProtocol.js"
  );
  assert.equal(publicTrip(""), "");
  assert.equal(publicTrip("  Ab12Cd  "), "!Ab12Cd");
  assert.equal(publicTrip("!Ab12Cd"), "!Ab12Cd");
  assert.equal(publicTrip("name#secret"), "");
  assert.equal(publicTrip("has space"), "");
  assert.equal(publicTrip("hunter2"), "");
  assert.equal(publicTrip("correct horse battery staple"), "");
  assert.equal(publicTrip("!!Ab12Cd"), "");

  const bare = joinFrame({ room: "lobby", nick: "Muse", trip: "" });
  assert.deepEqual(bare, { v: 1, type: "join", room: "lobby", nick: "Muse" });
  assert.equal("trip" in bare, false);

  const secret = joinFrame({ room: "lobby", nick: "Muse", trip: "hunter2" });
  assert.equal("trip" in secret, false);
  assert.equal(JSON.stringify(secret).includes("hunter2"), false);
  assert.equal(JSON.stringify(secret).includes("#"), false);

  const tripped = joinFrame({ room: "lobby", nick: "Muse", trip: "Ab12Cd" });
  assert.equal(tripped.trip, "!Ab12Cd");
  assert.equal(tripped.room, "lobby");
  assert.equal("password" in tripped, false);

  const chat = chatFrame("hello");
  assert.deepEqual(chat, { v: 1, type: "chat", text: "hello" });
  assert.equal("trip" in chat, false);

  assert.equal(isHello({ v: 1, type: "hello", protocol: PROTOCOL_NAME }), true);
  assert.equal(isHello({ v: 1, type: "hello", protocol: "other" }), false);
  assert.equal(isHello({ v: 2, type: "hello", protocol: PROTOCOL_NAME }), false);
  assert.equal(isHello({ type: "welcome" }), false);
});

test("public relay answers hello and does not require a join", async () => {
  const url = "wss://ws.voizel.com/relay";
  const health = await fetch("https://ws.voizel.com/health");
  assert.equal(health.status, 200);
  const body = await health.json();
  assert.equal(body.ok, true);
  assert.equal(body.protocol, "voizle-text-relay");
  assert.equal(body.v, 1);
  assert.equal("rooms" in body, true);
  for (const key of ["nick", "trip", "text", "room", "token"]) {
    assert.equal(Object.hasOwn(body, key), false, key);
  }

  const ws = new WebSocket(url);
  try {
    const hello = await new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error("timed out waiting for hello")), 8000);
      ws.addEventListener("message", (ev) => {
        clearTimeout(timer);
        try {
          resolve(JSON.parse(String(ev.data)));
        } catch (err) {
          reject(err);
        }
      });
      ws.addEventListener("error", () => {
        clearTimeout(timer);
        reject(new Error("socket error"));
      });
    });
    assert.equal(hello.type, "hello");
    assert.equal(hello.protocol, "voizle-text-relay");
    assert.equal(hello.v, 1);
    assert.equal(ws.readyState, WebSocket.OPEN);
  } finally {
    ws.close();
  }
});
