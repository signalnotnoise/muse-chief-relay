// Read-only room board for the Muse page. Loaded before app.js in the browser,
// and require()'d by tests/muse/board.test.js. docs/muse and web/muse copies
// of this file are kept identical.
//
// A board file is boards/<sha256(channel)>.jsonl. The page hashes the channel
// the user typed and fetches that file. It never writes the file. A last line
// with no newline is a record when it parses, and is ignored when it does not,
// so a writer still appending is not shown as a broken card.
(function (root, factory) {
  var api = factory();
  if (typeof module === "object" && module.exports) module.exports = api;
  else root.MuseBoard = api;
})(typeof globalThis !== "undefined" ? globalThis : this, function () {
  var RAW_ROOT = "https://raw.githubusercontent.com/signalnotnoise/muse-chief-relay/main/boards/";
  var TASK_STATES = { open: 1, claimed: 1, blocked: 1, done: 1 };
  var HASH_RE = /^[0-9a-f]{64}$/;

  function isInt(n) {
    return typeof n === "number" && Number.isInteger(n);
  }

  function isString(v) {
    return typeof v === "string";
  }

  // Copy only the fields in boards/schema.json. Anything else on the line
  // (including a password-shaped key) is dropped and never rendered.
  function parseRecord(obj) {
    if (!obj || typeof obj !== "object" || typeof obj.type !== "string") return null;
    if (obj.type === "task") {
      if (!isInt(obj.id) || !isString(obj.title) || !isString(obj.owner) || !TASK_STATES[obj.state])
        return null;
      var task = { type: "task", id: obj.id, title: obj.title, owner: obj.owner, state: obj.state };
      if (isString(obj.blocked_on)) task.blocked_on = obj.blocked_on;
      if (isString(obj.handoff_to)) task.handoff_to = obj.handoff_to;
      return task;
    }
    if (obj.type === "decision") {
      if (!isInt(obj.ts) || !isString(obj.decider) || !isString(obj.decision)) return null;
      var decision = { type: "decision", ts: obj.ts, decider: obj.decider, decision: obj.decision };
      if (isString(obj.context)) decision.context = obj.context;
      return decision;
    }
    if (obj.type === "scratch") {
      if (!isInt(obj.ts) || !isString(obj.author) || !isString(obj.text)) return null;
      return { type: "scratch", ts: obj.ts, author: obj.author, text: obj.text };
    }
    return null;
  }

  function emptyBoard() {
    return { tasks: [], decisions: [], scratch: [], skipped: 0 };
  }

  // Read-only parse. The last line is kept when it is a valid record even if
  // the file has no trailing newline. An unterminated line that does not parse
  // is ignored and not counted as skipped: someone else may still be writing it.
  function parseBoard(text) {
    var raw = String(text == null ? "" : text);
    if (raw.charCodeAt(0) === 0xfeff) raw = raw.slice(1);
    if (raw === "") return emptyBoard();
    var unterminated = !raw.endsWith("\n");
    var lines = raw.split("\n");
    if (!unterminated) lines.pop();
    var tasks = [];
    var taskAt = new Map();
    var decisions = [];
    var scratch = [];
    var skipped = 0;
    var last = lines.length - 1;
    for (var i = 0; i < lines.length; i++) {
      var line = lines[i];
      var partial = unterminated && i === last;
      if (line.endsWith("\r")) line = line.slice(0, -1);
      if (line === "") continue;
      var obj;
      try {
        obj = JSON.parse(line);
      } catch (e) {
        if (!partial) skipped++;
        continue;
      }
      var rec = parseRecord(obj);
      if (!rec) {
        if (!partial) skipped++;
        continue;
      }
      if (rec.type === "task") {
        if (taskAt.has(rec.id)) tasks[taskAt.get(rec.id)] = rec;
        else {
          taskAt.set(rec.id, tasks.length);
          tasks.push(rec);
        }
      } else if (rec.type === "decision") {
        decisions.push(rec);
      } else {
        scratch.push(rec);
      }
    }
    return { tasks: tasks, decisions: decisions, scratch: scratch, skipped: skipped };
  }

  function toHex(buffer) {
    var bytes = new Uint8Array(buffer);
    var hex = "";
    for (var i = 0; i < bytes.length; i++) {
      var h = bytes[i].toString(16);
      hex += h.length === 1 ? "0" + h : h;
    }
    return hex;
  }

  // Lowercase hex SHA-256 of the trimmed channel. Null when crypto.subtle
  // is missing (a page that is not HTTPS and not localhost).
  async function boardFileFor(channel) {
    var cryptoObj = globalThis.crypto;
    if (!cryptoObj || !cryptoObj.subtle || typeof cryptoObj.subtle.digest !== "function") return null;
    var digest = await cryptoObj.subtle.digest("SHA-256", new TextEncoder().encode(channel.trim()));
    return toHex(digest);
  }

  // Same-origin ../../boards/<hash>.jsonl only when this page is two levels
  // under the repo root (web/muse or docs/muse). file: cannot fetch that path
  // reliably, and on GitHub Pages the same relative URL leaves the project site.
  function sameOriginOk(pageUrl) {
    var page;
    try { page = new URL(pageUrl); }
    catch (e) { return false; }
    if (page.protocol === "file:") return false;
    var host = page.hostname || "";
    if (host === "github.io" || host.endsWith(".github.io")) return false;
    var parts = page.pathname.split("/").filter(function (p) { return p !== ""; });
    if (parts.length !== 2 && parts.length !== 3) return false;
    if (parts[0] !== "web" && parts[0] !== "docs") return false;
    if (parts[1] !== "muse") return false;
    return true;
  }

  function boardSources(hash, pageUrl, cacheBust) {
    if (typeof hash !== "string" || !HASH_RE.test(hash)) {
      throw new Error("bad board hash");
    }
    var file = hash + ".jsonl";
    var sources = [];
    if (pageUrl && sameOriginOk(pageUrl)) {
      try {
        var rel = new URL("../../boards/" + file, pageUrl);
        if (cacheBust) rel.searchParams.set("t", String(cacheBust));
        sources.push(rel.href);
      } catch (e) { /* a bad base URL just means we use the raw file */ }
    }
    var raw = RAW_ROOT + file;
    if (cacheBust) raw += "?t=" + encodeURIComponent(String(cacheBust));
    sources.push(raw);
    return sources;
  }

  function viaFor(url) {
    return String(url).indexOf("raw.githubusercontent.com") !== -1 ? "GitHub raw on main" : "this repo";
  }

  // GET each source until one returns a board. HTML is not a board. Every
  // 404 and no other failure is "missing". A thrown fetch, with nothing read,
  // is "error". fetchImpl is for tests; the page passes nothing and uses fetch.
  async function loadBoardText(sources, fetchImpl) {
    var fetchFn = fetchImpl || globalThis.fetch;
    var saw404 = false;
    var sawError = false;
    var list = Array.isArray(sources) ? sources : [];
    if (typeof fetchFn !== "function") return { status: "error", text: "", via: "" };
    for (var i = 0; i < list.length; i++) {
      var url = list[i];
      try {
        var res = await fetchFn(url, { method: "GET", cache: "no-store", credentials: "omit" });
        if (res && res.status === 404) {
          saw404 = true;
          continue;
        }
        if (!res || !res.ok) {
          sawError = true;
          continue;
        }
        var ctype = "";
        if (res.headers && typeof res.headers.get === "function") {
          ctype = String(res.headers.get("content-type") || "").toLowerCase();
        }
        if (ctype.indexOf("text/html") !== -1) {
          sawError = true;
          continue;
        }
        var text = await res.text();
        if (String(text).trimStart().startsWith("<")) {
          sawError = true;
          continue;
        }
        return { status: "ok", text: text, via: viaFor(url) };
      } catch (err) {
        sawError = true;
      }
    }
    if (saw404 && !sawError) return { status: "missing", text: "", via: "" };
    return { status: "error", text: "", via: "" };
  }

  return {
    parseBoard: parseBoard,
    boardFileFor: boardFileFor,
    boardSources: boardSources,
    loadBoardText: loadBoardText
  };
});
