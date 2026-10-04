const test = require("node:test");
const assert = require("node:assert/strict");

test("demo config prefers the query, then storage, then the public demo relay", async () => {
  const demo = await import("../../web/muse/src/demoSession.js");
  assert.equal(demo.publicDemoRelay(undefined), "");
  assert.equal(demo.publicDemoRelay("  "), "");
  assert.equal(demo.describePublicDemoRelay(""), "unset");
  assert.equal(demo.describePublicDemoRelay("wss://relay.example.com/relay"), "wss://relay.example.com/relay");
  assert.equal(demo.resolveVisitorRelay(""), "");
  assert.equal(demo.resolveVisitorRelay("   "), "");
  assert.equal(demo.resolveVisitorRelay("wss://relay.example.com/relay"), "wss://relay.example.com/relay");
  assert.equal(demo.resolveVisitorRelay("wss://user:secret@relay.example.com/relay"), "");

  const storage = new Map();
  const box = {
    getItem: (key) => (storage.has(key) ? storage.get(key) : null),
    setItem: (key, value) => storage.set(key, value),
    removeItem: (key) => storage.delete(key),
  };
  demo.saveDemoConfig(box, { relay: "wss://relay.example.com/relay", room: "your-channel-name" });
  assert.equal(storage.get(demo.DEMO_RELAY_KEY), "wss://relay.example.com/relay");
  assert.equal(storage.get(demo.DEMO_ROOM_KEY), "your-channel-name");
  assert.equal([...storage.keys()].some((key) => /trip|password|token/.test(key)), false);

  const fromStore = demo.loadDemoConfig({ search: "", storage: box, publicRelay: "" });
  assert.equal(fromStore.relay, "wss://relay.example.com/relay");
  assert.equal(fromStore.room, "your-channel-name");

  const fromQuery = demo.loadDemoConfig({
    search: "?relay=ws://127.0.0.1:8787/relay&room=lobby",
    storage: box,
    publicRelay: "wss://relay.example.com/relay",
  });
  assert.equal(fromQuery.relay, "ws://127.0.0.1:8787/relay");
  assert.equal(fromQuery.room, "lobby");
  assert.equal(fromQuery.fromQuery, true);

  const fromPublic = demo.loadDemoConfig({
    search: "",
    storage: { getItem: () => "" },
    publicRelay: "wss://relay.example.com/relay",
  });
  assert.equal(fromPublic.relay, "wss://relay.example.com/relay");
  assert.equal(fromPublic.room, "");

  const unset = demo.loadDemoConfig({ search: "", storage: { getItem: () => "" }, publicRelay: "" });
  assert.equal(unset.relay, "");
  assert.equal(unset.room, "");
});
