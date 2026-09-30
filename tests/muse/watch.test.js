const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("fs");
const path = require("path");

const srcDir = path.join(__dirname, "../../web/muse/src");
const published = path.join(__dirname, "../../docs/muse");

test("watch format helpers", async () => {
  const f = await import("../../web/muse/src/watchFormat.js");

  assert.equal(f.roleOf("Alex"), "human");
  assert.equal(f.roleOf("Fuse"), "agent");
  assert.equal(f.roleOf("chief"), "agent");
  assert.equal(f.roleOf("Design"), "agent");
  assert.equal(f.roleOf("spectator_ab12"), "spectator");
  assert.equal(f.roleOf("spectator-ab12"), "spectator");
  assert.equal(f.roleOf("stranger"), "guest");

  assert.deepEqual(f.nickStyle("Fuse"), { fg: "#7ee0b0", bg: "rgba(126,224,176,0.12)" });
  assert.deepEqual(f.nickStyle("nobody-in-particular"), f.nickStyle("nobody-in-particular"));

  const task = f.parseEnvelope(JSON.stringify({ type: "task", id: "t1", title: "Hi" }));
  assert.equal(task.type, "task");
  assert.equal(task.id, "t1");
  assert.equal(f.parseEnvelope("hello world"), null);
  assert.equal(f.parseEnvelope("{not json"), null);
  assert.equal(f.parseEnvelope(JSON.stringify({ cmd: "chat" })), null);
  assert.equal(f.parseEnvelope(""), null);

  assert.match(f.spectatorNick(), /^spectator_[a-z0-9]{4}$/);
  assert.equal(f.formatTrip("!Ab12Cd"), "!Ab12Cd");
  assert.equal(f.formatTrip("Ab12Cd"), "!Ab12Cd");
  assert.equal(f.formatTrip(""), "");
});

test("watch route wiring", async () => {
  const { isWatchRoute } = await import("../../web/muse/src/watchRoute.js");
  assert.equal(isWatchRoute("#/watch"), true);
  assert.equal(isWatchRoute("#/watch/"), true);
  assert.equal(isWatchRoute("#/watchdog"), false);
  assert.equal(isWatchRoute("#/watch-anything"), false);
  assert.equal(isWatchRoute("#/Watch"), false);
  assert.equal(isWatchRoute("#/"), false);
  assert.equal(isWatchRoute(""), false);

  const main = fs.readFileSync(path.join(srcDir, "main.js"), "utf8");
  assert.match(main, /isWatchRoute/);
  assert.match(main, /WatchLive/);
  assert.doesNotMatch(main, /startsWith/);
  assert.doesNotMatch(main, /location\.hash\s*=/);
  assert.ok(fs.existsSync(path.join(srcDir, "WatchLive.vue")));
  assert.ok(fs.existsSync(path.join(srcDir, "useWatch.js")));
  assert.ok(fs.existsSync(path.join(srcDir, "watchFormat.js")));
});

test("watch view joins the relay channel from build config as a read-only spectator", async () => {
  const { resolveWatchChannel } = await import("../../web/muse/src/watchChannel.js");
  assert.equal(resolveWatchChannel(undefined), "");
  assert.equal(resolveWatchChannel(null), "");
  assert.equal(resolveWatchChannel(""), "");
  assert.equal(resolveWatchChannel("   "), "");
  assert.equal(resolveWatchChannel("  room-name  "), "room-name");

  const w = fs.readFileSync(path.join(srcDir, "useWatch.js"), "utf8");
  assert.match(w, /resolveWatchChannel/);
  assert.match(w, /VITE_WATCH_CHANNEL/);
  assert.match(w, /VITE_RELAY_URL/);
  assert.match(w, /watch channel not configured/);
  assert.match(w, /joinFrame/);
  assert.match(w, /isHello/);
  assert.doesNotMatch(w, /VITE_RELAY_CHANNEL/);
  assert.doesNotMatch(w, /hack\.chat/);
  assert.doesNotMatch(w, /cmd:\s*"join"/);
  assert.doesNotMatch(w, /location\.search/);
  assert.match(w, /spectatorNick/);
  assert.doesNotMatch(w, /console\./);
  const openBody = w.slice(w.indexOf("sock.onopen"), w.indexOf("sock.onmessage"));
  assert.doesNotMatch(openBody, /setStatus\(\s*"live"/);
  assert.match(w, /decision\.action === "joined"/);
  assert.match(w, /setStatus\(\s*"live",\s*true\s*\)/);
  assert.match(w, /decision\.action === "retry"/);
  assert.match(w, /decision\.action === "giveup"/);
  assert.match(w, /decision\.rotateNick/);
  const v = fs.readFileSync(path.join(srcDir, "WatchLive.vue"), "utf8");
  assert.match(v, /multi-agent relay/);
  assert.match(v, /TransitionGroup/);
  const template = v.slice(v.indexOf("<template>"), v.indexOf("</template>"));
  const root = template.match(/<div class="([^"]*)">/);
  assert.ok(root, "watch root element");
  assert.match(root[1], /\bh-full\b/);
  assert.match(root[1], /\boverflow-y-auto\b/);
  assert.doesNotMatch(root[1], /\bmin-h-dvh\b/);
});

test("a pre-join error is a rejected join; live only after welcome", async () => {
  const { onWatchFrame } = await import("../../web/muse/src/watchSession.js");

  const taken = onWatchFrame({ v: 1, type: "error", code: "nick_taken", text: "nick is in use" }, false);
  assert.equal(taken.action, "retry");
  assert.equal(taken.live, false);
  assert.equal(taken.joined, false);
  assert.equal(taken.rotateNick, true);

  const format = onWatchFrame({ v: 1, type: "error", code: "invalid_nick", text: "nick must be 1-24 characters" }, false);
  assert.equal(format.action, "giveup");
  assert.equal(format.live, false);
  assert.equal(format.rotateNick, false);

  const rate = onWatchFrame({ v: 1, type: "error", code: "rate_limited", text: "too many chat lines" }, false);
  assert.equal(rate.action, "retry");
  assert.equal(rate.live, false);
  assert.equal(rate.rotateNick, false);

  const hello = onWatchFrame({ v: 1, type: "hello", protocol: "voizle-text-relay" }, false);
  assert.equal(hello.action, "stay");
  assert.equal(hello.live, false);

  const joined = onWatchFrame({ v: 1, type: "welcome", users: [{ nick: "spectator_ab12" }] }, false);
  assert.equal(joined.action, "joined");
  assert.equal(joined.live, true);
  assert.equal(joined.joined, true);

  const later = onWatchFrame({ v: 1, type: "error", code: "nick_taken", text: "nick is in use" }, true);
  assert.equal(later.action, "stay");
  assert.equal(later.live, true);
  assert.equal(later.rotateNick, false);

  const chat = onWatchFrame({ v: 1, type: "chat", nick: "Fuse", text: "hi" }, true);
  assert.equal(chat.action, "stay");
  assert.equal(chat.live, true);
});

test("the Pages build includes the watch-live view", async () => {
  const index = path.join(published, "index.html");
  assert.ok(fs.existsSync(index), "docs/muse/index.html missing; run npm run build in web/muse");
  let text = "";
  const walk = (dir) => {
    for (const name of fs.readdirSync(dir)) {
      const p = path.join(dir, name);
      if (fs.statSync(p).isDirectory()) walk(p);
      else if (/\.(html|js|css)$/.test(name)) text += fs.readFileSync(p, "utf8");
    }
  };
  walk(published);
  assert.match(text, /multi-agent relay/);
  assert.match(text, /#\/watch/);
  assert.match(text, /watch channel not configured/);
  assert.match(text, /VITE_WATCH_CHANNEL/);
  assert.match(text, /voizle-text-relay/);
  assert.match(text, /ws:\/\/127\.0\.0\.1:8787\/relay/);
  assert.doesNotMatch(text, /hack\.chat/);
  assert.doesNotMatch(text, /wss:\/\/ws\.voizel\.com/);
  assert.doesNotMatch(text, /startsWith\("#\/watch"\)|startsWith\('#\/watch'\)/);
});
