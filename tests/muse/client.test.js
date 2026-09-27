const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("fs");
const path = require("path");

const srcDir = path.join(__dirname, "../../web/muse/src");
const appVue = path.join(srcDir, "App.vue");
const useChat = path.join(srcDir, "useChat.js");
const published = path.join(__dirname, "../../docs/muse");

test("the Muse page is styled with Tailwind", () => {
  const css = fs.readFileSync(path.join(srcDir, "styles.css"), "utf8");
  const vite = fs.readFileSync(path.join(srcDir, "../vite.config.mjs"), "utf8");
  const app = fs.readFileSync(appVue, "utf8");
  assert.match(css, /@import\s+"tailwindcss"/);
  assert.match(vite, /@tailwindcss\/vite/);
  assert.match(app, /min-h-0/);
  assert.match(app, /overflow-y-auto/);
  assert.match(app, /id="send-form"/);
});

test("the Vue client uses the shared reconnect and scroll modules", () => {
  const text = fs.readFileSync(useChat, "utf8");
  assert.match(text, /from\s+"\.\/reconnect\.js"/);
  assert.match(text, /from\s+"\.\/scroll\.js"/);
});

test("join fields have no name attribute", () => {
  const vue = fs.readFileSync(appVue, "utf8");
  const start = vue.indexOf('id="join-form"');
  const end = vue.indexOf('id="chat-panel"');
  assert.ok(start > 0 && end > start);
  const form = vue.slice(start, end);
  assert.doesNotMatch(form, /\sname\s*=/);
  assert.match(form, /type="password"/);
  assert.match(form, /id="password"/);
});

test("muse source does not log or touch browser storage", () => {
  for (const name of fs.readdirSync(srcDir)) {
    if (!/\.(js|vue|css)$/.test(name)) continue;
    const text = fs.readFileSync(path.join(srcDir, name), "utf8");
    assert.doesNotMatch(text, /console\./, name);
    assert.doesNotMatch(text, /localStorage|sessionStorage|document\.cookie|history\.pushState|location\.(href|search|hash)\s*=/, name);
  }
});

function readPublished() {
  assert.ok(fs.existsSync(path.join(published, "index.html")), "docs/muse/index.html missing; run npm run build in web/muse");
  let text = "";
  const walk = (dir) => {
    for (const name of fs.readdirSync(dir)) {
      const p = path.join(dir, name);
      if (fs.statSync(p).isDirectory()) walk(p);
      else if (/\.(html|js|css)$/.test(name)) text += fs.readFileSync(p, "utf8");
    }
  };
  walk(published);
  return text;
}

test("the Pages build is the Vite output, not a second copy of the old client", () => {
  const text = readPublished();
  assert.match(text, /id="app"/);
  assert.match(text, /<script/);
  assert.match(text, /reconnecting in /);
  assert.match(text, /your-channel-name/);
  assert.match(text, /with a trip password/);
  assert.match(text, /\.overflow-y-auto/);
  assert.match(text, /\.min-h-0/);
  assert.match(text, /Join a channel to see its room board/);
  assert.match(text, /No board for this channel yet/);
  const index = fs.readFileSync(path.join(published, "index.html"), "utf8");
  assert.match(index, /src="\.\/assets\//);
  assert.match(index, /href="\.\/assets\//);
  assert.doesNotMatch(index, /src="\/assets\//);
  assert.equal(fs.existsSync(path.join(published, "app.js")), false);
  assert.equal(fs.existsSync(path.join(published, "board.js")), false);
  assert.equal(fs.existsSync(path.join(published, "reconnect.js")), false);
  assert.doesNotMatch(text, /localStorage|sessionStorage/);
});
