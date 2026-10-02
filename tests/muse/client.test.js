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

test("the attention queue renders above the transcript in the chat view", () => {
  const app = fs.readFileSync(appVue, "utf8");
  assert.match(app, /<AttentionQueue/);
  assert.match(app, /attentionItems/);
  assert.match(app, /clearAttentionItem/);
  assert.match(app, /id="attention-queue"|AttentionQueue\.vue/);
  const chat = fs.readFileSync(useChat, "utf8");
  assert.match(chat, /from\s+"\.\/attentionQueue\.js"/);
  assert.match(chat, /clearAttentionItem/);
  const queue = fs.readFileSync(path.join(srcDir, "AttentionQueue.vue"), "utf8");
  assert.match(queue, /id="attention-queue"/);
  assert.match(queue, /Needs Alex/);
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
  // The join form intentionally has a masked password field now (join
  // passwords, voizle#7): its id is "join-password", never "password".
  assert.doesNotMatch(form, /id="password"/);
  assert.match(form, /id="join-password"/);
  assert.match(form, /type="password"/);
  assert.match(form, /id="trip"/);
  assert.match(form, /type="text"/);
  assert.match(form, /Not your password/);
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
  assert.match(text, /with a public trip/);
  assert.match(text, /voizle-text-relay/);
  assert.match(text, /ws:\/\/127\.0\.0\.1:8787\/relay/);
  assert.doesNotMatch(text, /hack\.chat/);
  assert.doesNotMatch(text, /wss:\/\/ws\.voizel\.com/);
  assert.match(text, /\.overflow-y-auto/);
  assert.match(text, /\.min-h-0/);
  assert.match(text, /Join a channel to see its room board/);
  assert.match(text, /No board for this channel yet/);
  assert.match(text, /attention-queue/);
  assert.match(text, /Needs Alex/);
  const index = fs.readFileSync(path.join(published, "index.html"), "utf8");
  assert.match(index, /src="\.\/assets\//);
  assert.match(index, /href="\.\/assets\//);
  assert.doesNotMatch(index, /src="\/assets\//);
  assert.equal(fs.existsSync(path.join(published, "app.js")), false);
  assert.equal(fs.existsSync(path.join(published, "board.js")), false);
  assert.equal(fs.existsSync(path.join(published, "reconnect.js")), false);
  assert.doesNotMatch(text, /localStorage|sessionStorage/);
});

test("narrow sidebar never clips Disconnect and keeps the user list usable", () => {
  const app = fs.readFileSync(appVue, "utf8");
  // The old max-h-40 cap clipped the sidebar on phones, pushing Disconnect
  // out of reach. The cap is gone entirely.
  assert.doesNotMatch(app, /max-\[820px\]:max-h-/);
  // On narrow widths the users list becomes a horizontal chip row instead of
  // a crushed vertical list.
  assert.match(app, /id="users"[^>]*max-\[820px\]:overflow-x-auto/);
  // Disconnect is still a plain in-flow button after the list.
  const disconnectAt = app.indexOf('id="disconnect"');
  const usersAt = app.indexOf('id="users"');
  assert.ok(disconnectAt > usersAt, "Disconnect must come after the users list");
  assert.match(app.slice(disconnectAt, disconnectAt + 400), /focus-visible:/);
});

test("connected chat has an h1", () => {
  const app = fs.readFileSync(appVue, "utf8");
  const panel = app.slice(app.indexOf('id="chat-panel"'));
  assert.match(panel, /<h1[^>]*>Relay chat<\/h1>/);
});

test("join-card footer uses text-muted, not low-contrast text-dim", () => {
  const app = fs.readFileSync(appVue, "utf8");
  const marker = app.indexOf("voizle-text-relay");
  assert.ok(marker > 0);
  const footer = app.slice(app.lastIndexOf("<p", marker), app.indexOf("</p>", marker));
  assert.match(footer, /text-muted/);
  assert.doesNotMatch(footer, /text-dim/);
});

test("new header, hero, and sidebar controls have focus-visible styles", () => {
  const app = fs.readFileSync(appVue, "utf8");
  const header = fs.readFileSync(path.join(srcDir, "SiteHeader.vue"), "utf8");
  const tabs = header.slice(header.indexOf('v-for="t in tabs"'), header.indexOf("</nav>"));
  assert.match(tabs, /focus-visible:outline-focus/);
  const headerGithub = header.slice(header.indexOf('id="status"'));
  assert.match(headerGithub, /focus-visible:outline-focus/);
  const boardLink = app.slice(app.indexOf('href="#/board"'));
  assert.match(boardLink.slice(0, 500), /focus-visible:outline-focus/);
  const hero = app.slice(app.indexOf("Watch live →") - 400, app.indexOf("Watch live →"));
  assert.match(hero, /focus-visible:outline-focus/);
});

test("board badge counts open or claimed tasks, matching its comment", () => {
  const app = fs.readFileSync(appVue, "utf8");
  const header = fs.readFileSync(path.join(srcDir, "SiteHeader.vue"), "utf8");
  assert.match(app, /t\.state === "open" \|\| t\.state === "claimed"/);
  assert.match(header, /open or claimed board tasks/);
});

test("sidebar board link hides on narrow widths (nothing board-like above phone chat)", () => {
  const app = fs.readFileSync(appVue, "utf8");
  const linkAt = app.indexOf("Room board →");
  assert.ok(linkAt > 0, "sidebar board link still exists for desktop");
  assert.match(app.slice(linkAt - 500, linkAt), /max-\[820px\]:hidden/);
});

test("clicking a transcript nick mentions it in the composer", () => {
  const app = fs.readFileSync(appVue, "utf8");
  // The nick is a real button now (focusable, keyboard-operable), wired to
  // the mention handler; sys rows (no nick) still render no button.
  assert.match(app, /@click="mentionNick\(row\.nick\)"/);
  assert.match(app, /:title="'Mention ' \+ row\.nick"/);
  assert.match(app, /mentionNick,/);
  const chat = fs.readFileSync(useChat, "utf8");
  assert.match(chat, /function mentionNick\(name\)/);
  assert.match(chat, /from\s+"\.\/composerHistory\.js"/);
  // The mention inserts "@nick " at the caret and focuses the composer.
  assert.match(chat, /"@" \+ who \+ " "/);
  assert.match(chat, /setSelectionRange\(caret, caret\)/);
});

test("clicking an Online sidebar name mentions it in the composer", () => {
  const app = fs.readFileSync(appVue, "utf8");
  // Each roster name (except the user's own row) is a real button wired to
  // the same mention handler as transcript nicks.
  assert.match(app, /@click="mentionNick\(name\)"/);
  assert.match(app, /:title="'Mention ' \+ name"/);
  assert.match(app, /v-if="name !== nick"/);
});

test("chat transcript shows an unread divider and a jump pill when scrolled up", () => {
  const app = fs.readFileSync(appVue, "utf8");
  assert.match(app, /unreadCount/);
  assert.match(app, /firstUnreadId/);
  assert.match(app, /v-if="row\.id === firstUnreadId && unreadCount > 0"/);
  assert.match(app, /New messages/);
  assert.match(app, /@click="jumpToLatest"/);
  assert.match(app, /\{\{ unreadCount \}\} new/);
  assert.match(app, /jumpToLatest,/);
  const chat = fs.readFileSync(useChat, "utf8");
  // Rows arriving while the reader is up the transcript count as unread;
  // any return to the tail (scroll, send, pill tap, fresh join) clears.
  assert.match(chat, /unreadCount\.value \+= 1/);
  assert.match(chat, /firstUnreadId\.value = id/);
  assert.match(chat, /function clearUnread\(\)/);
  assert.match(chat, /function jumpToLatest\(\)/);
  // The published fallback bundle carries the new affordance.
  const published = readPublished();
  assert.match(published, /New messages/);
});

test("composer recalls sent messages with ArrowUp/ArrowDown", () => {
  const app = fs.readFileSync(appVue, "utf8");
  assert.match(app, /@keydown="onComposerKeydown"/);
  assert.match(app, /onComposerKeydown,/);
  const chat = fs.readFileSync(useChat, "utf8");
  assert.match(chat, /function onComposerKeydown\(ev\)/);
  assert.match(chat, /composerHistory\.step\(ev\.key === "ArrowUp" \? -1 : 1/);
  // Sends are recorded; typing abandons an in-progress browse.
  assert.match(chat, /composerHistory\.push\(text\)/);
  assert.match(chat, /composerHistory\.cancel\(\)/);
  // History never touches browser storage (the suite's guardrail covers src,
  // and the module itself must stay DOM-free and console-free).
  const hist = fs.readFileSync(path.join(srcDir, "composerHistory.js"), "utf8");
  assert.doesNotMatch(hist, /localStorage|sessionStorage|document|window|console\./);
});
