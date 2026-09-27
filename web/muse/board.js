// Read-only room board for the Muse page. Loaded before app.js in the browser,
// and require()'d by tests/muse/board.test.js. docs/muse and web/muse copies
// of this file are kept identical.
//
// The page reads boards/<room>.jsonl. It never writes that file and never
// treats the last line as its own: only newline-terminated lines are records,
// so a trailing partial line (someone else still appending) is left untouched.
(function (root, factory) {
  var api = factory();
  if (typeof module === "object" && module.exports) module.exports = api;
  else root.MuseBoard = api;
})(typeof globalThis !== "undefined" ? globalThis : this, function () {
  // This room's committed board. A file path, not a hack.chat channel:
  // nothing here is copied into the channel field.
  var BOARD_FILE = "boards/fuse-grok-6f4e970cd8.jsonl";
  // Pages publishes docs/ and does not serve boards/, so the published client
  // falls back to the file on main. raw.githubusercontent.com sends ACAO: *.
  var RAW_URL = "https://raw.githubusercontent.com/signalnotnoise/muse-chief-relay/main/" + BOARD_FILE;
  var TASK_STATES = { open: 1, claimed: 1, blocked: 1, done: 1 };

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

  // Read-only parse. A file that does not end in a newline has an incomplete
  // last line; that line is not a record. This page does not own it.
  function parseBoard(text) {
    var raw = String(text == null ? "" : text);
    if (raw.charCodeAt(0) === 0xfeff) raw = raw.slice(1);
    var empty = { tasks: [], decisions: [], scratch: [], skipped: 0 };
    var end = raw.endsWith("\n") ? raw.length : raw.lastIndexOf("\n");
    if (end < 0) return empty;
    var lines = raw.slice(0, end).split("\n");
    var tasks = [];
    var taskAt = new Map();
    var decisions = [];
    var scratch = [];
    var skipped = 0;
    for (var i = 0; i < lines.length; i++) {
      var line = lines[i];
      if (line === "" || line === "\r") continue;
      if (line.endsWith("\r")) line = line.slice(0, -1);
      if (line === "") continue;
      var obj;
      try {
        obj = JSON.parse(line);
      } catch (e) {
        skipped++;
        continue;
      }
      var rec = parseRecord(obj);
      if (!rec) {
        skipped++;
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

  // Same-origin first when the static root is the repo: both web/muse and
  // docs/muse sit two levels below it. Then the committed file on main.
  // cacheBust is for Reload; 0 leaves the URL alone.
  function boardSources(pageUrl, cacheBust) {
    var sources = [];
    if (pageUrl) {
      try {
        var rel = new URL("../../" + BOARD_FILE, pageUrl);
        if (cacheBust) rel.searchParams.set("t", String(cacheBust));
        sources.push(rel.href);
      } catch (e) { /* a bad base URL just means we use the raw file */ }
    }
    var raw = RAW_URL;
    if (cacheBust) raw += "?t=" + encodeURIComponent(String(cacheBust));
    sources.push(raw);
    return sources;
  }

  return {
    BOARD_FILE: BOARD_FILE,
    RAW_URL: RAW_URL,
    parseBoard: parseBoard,
    boardSources: boardSources
  };
});
