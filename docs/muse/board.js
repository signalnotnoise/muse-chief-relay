// Read-only room board for the Muse client. Loaded beside app.js in the browser,
// and require()'d by tests/muse/board.test.js. docs/muse and web/muse copies of
// this file are kept identical.
//
// The page GETs boards/<room>.jsonl and renders tasks, decisions, and scratch.
// It never writes that file, the public status page, or anyone's knowledge graph.
(function (root, factory) {
  var api = factory();
  if (typeof module === "object" && module.exports) module.exports = api;
  else root.MuseBoard = api;
})(typeof globalThis !== "undefined" ? globalThis : this, function () {
  var DEFAULT_ROOM = "fuse-grok-6f4e970cd8";
  // Room file names are a single path segment. Reject "..", slashes, and URLs
  // so a ?board= query cannot point the GET anywhere but boards/<room>.jsonl.
  var ROOM_RE = /^[A-Za-z0-9][A-Za-z0-9._-]{0,80}$/;
  var TASK_STATES = { open: 1, claimed: 1, blocked: 1, done: 1 };

  function safeRoom(room) {
    if (!ROOM_RE.test(room || "")) return DEFAULT_ROOM;
    // ".." is never a single step down into boards/, even as part of a name.
    if (room.indexOf("..") !== -1) return DEFAULT_ROOM;
    return room;
  }

  function roomFromSearch(search) {
    var q = new URLSearchParams(String(search || "").replace(/^\?/, ""));
    return safeRoom(q.get("board"));
  }

  function boardUrl(room) {
    return "../../boards/" + safeRoom(room) + ".jsonl";
  }

  function boardPath(room) {
    return "boards/" + safeRoom(room) + ".jsonl";
  }

  function isStr(v) {
    return typeof v === "string";
  }

  function isUnix(v) {
    return typeof v === "number" && Number.isInteger(v);
  }

  function optStr(obj, key) {
    if (!Object.prototype.hasOwnProperty.call(obj, key) || obj[key] == null) return true;
    return isStr(obj[key]);
  }

  function asTask(obj) {
    if (!obj || obj.type !== "task") return null;
    if (typeof obj.id !== "number" || !Number.isInteger(obj.id) || obj.id < 1) return null;
    if (!isStr(obj.title) || !obj.title) return null;
    if (!isStr(obj.owner)) return null;
    if (!TASK_STATES[obj.state]) return null;
    if (!optStr(obj, "blocked_on") || !optStr(obj, "handoff_to")) return null;
    return obj;
  }

  function asDecision(obj) {
    if (!obj || obj.type !== "decision") return null;
    if (!isUnix(obj.ts) || !isStr(obj.decider) || !isStr(obj.decision)) return null;
    if (!optStr(obj, "context")) return null;
    return obj;
  }

  function asScratch(obj) {
    if (!obj || obj.type !== "scratch") return null;
    if (!isUnix(obj.ts) || !isStr(obj.author) || !isStr(obj.text)) return null;
    return obj;
  }

  // One record per line. A later task with the same id replaces the earlier one
  // (the file is append-only). Blank lines are ignored. Anything else is skipped.
  function parseJsonl(text) {
    var tasks = new Map();
    var taskOrder = [];
    var decisions = [];
    var scratch = [];
    var skipped = 0;
    var lines = String(text == null ? "" : text).split(/\r?\n/);
    for (var i = 0; i < lines.length; i++) {
      var line = lines[i].trim();
      if (!line) continue;
      var obj;
      try {
        obj = JSON.parse(line);
      } catch (e) {
        skipped++;
        continue;
      }
      var task = asTask(obj);
      if (task) {
        if (!tasks.has(task.id)) taskOrder.push(task.id);
        tasks.set(task.id, task);
        continue;
      }
      var decision = asDecision(obj);
      if (decision) {
        decisions.push(decision);
        continue;
      }
      var note = asScratch(obj);
      if (note) {
        scratch.push(note);
        continue;
      }
      skipped++;
    }
    return {
      tasks: taskOrder.map(function (id) { return tasks.get(id); }),
      decisions: decisions,
      scratch: scratch,
      skipped: skipped,
      empty: taskOrder.length === 0 && decisions.length === 0 && scratch.length === 0
    };
  }

  function formatTs(ts) {
    if (!isUnix(ts)) return "";
    var d = new Date(ts * 1000);
    if (isNaN(d.getTime())) return "";
    return d.toISOString().replace("T", " ").replace(/\.\d{3}Z$/, " UTC");
  }

  function viewModel(parsed) {
    var p = parsed || parseJsonl("");
    return {
      tasks: p.tasks.map(function (t) {
        var bits = ["#" + t.id, t.owner || ""];
        if (t.blocked_on) bits.push("blocked on " + t.blocked_on);
        if (t.handoff_to) bits.push("handoff to " + t.handoff_to);
        return { title: t.title, state: t.state, meta: bits.join(" · ") };
      }),
      decisions: p.decisions.map(function (d) {
        var who = d.decider + (formatTs(d.ts) ? " · " + formatTs(d.ts) : "");
        return { text: d.decision, meta: who, context: d.context || "" };
      }),
      scratch: p.scratch.map(function (s) {
        var who = s.author + (formatTs(s.ts) ? " · " + formatTs(s.ts) : "");
        return { text: s.text, meta: who };
      }),
      skipped: p.skipped,
      empty: p.empty
    };
  }

  function noteFor(kind, room, detail) {
    var path = boardPath(room);
    if (kind === "loading") return "Loading board…";
    if (kind === "missing")
      return "No board file at " + path + " (or this host can't see boards/). Nothing to show.";
    if (kind === "empty") return "The board file is empty.";
    if (kind === "unreadable")
      return "Nothing readable on the board. Skipped " + detail + " line(s).";
    if (kind === "skipped")
      return "Read-only. Skipped " + detail + " unreadable line(s).";
    if (kind === "ready") return "Read-only. This page does not write the board.";
    return "Couldn't read the board (" + (detail || "error") + "). Nothing to show.";
  }

  function el(doc, tag, cls, text) {
    var n = doc.createElement(tag);
    if (cls) n.className = cls;
    if (text != null) n.textContent = String(text);
    return n;
  }

  function fill(doc, id, rows, make, emptyText) {
    var ul = doc.getElementById(id);
    if (!ul) return;
    while (ul.firstChild) ul.removeChild(ul.firstChild);
    if (!rows.length) {
      ul.appendChild(el(doc, "li", "board-empty", emptyText));
      return;
    }
    for (var i = 0; i < rows.length; i++) ul.appendChild(make(rows[i]));
  }

  function render(doc, parsed) {
    var vm = viewModel(parsed);
    fill(doc, "board-tasks", vm.tasks, function (t) {
      var li = el(doc, "li", "board-card");
      var top = el(doc, "div", "board-card-top");
      top.appendChild(el(doc, "span", "board-title", t.title));
      var state = TASK_STATES[t.state] ? t.state : "open";
      top.appendChild(el(doc, "span", "board-state s-" + state, state));
      li.appendChild(top);
      li.appendChild(el(doc, "div", "board-meta", t.meta));
      return li;
    }, "No tasks.");
    fill(doc, "board-decisions", vm.decisions, function (d) {
      var li = el(doc, "li", "board-card");
      li.appendChild(el(doc, "div", "board-text", d.text));
      li.appendChild(el(doc, "div", "board-meta", d.meta));
      if (d.context) li.appendChild(el(doc, "div", "board-context", d.context));
      return li;
    }, "No decisions.");
    fill(doc, "board-scratch", vm.scratch, function (s) {
      var li = el(doc, "li", "board-card");
      li.appendChild(el(doc, "div", "board-text", s.text));
      li.appendChild(el(doc, "div", "board-meta", s.meta));
      return li;
    }, "No scratch.");
  }

  // Browser-only. Node tests require() this file and must not fetch.
  function mount(doc) {
    if (!doc || !doc.getElementById) return;
    var note = doc.getElementById("board-note");
    if (!note || typeof fetch !== "function") return;
    var search = "";
    try { search = doc.location.search || ""; } catch (e) { search = ""; }
    var room = roomFromSearch(search);
    var pathEl = doc.getElementById("board-path");
    if (pathEl) pathEl.textContent = boardPath(room);
    note.textContent = noteFor("loading");
    // GET only. No body, no method override, no write to the board file.
    fetch(boardUrl(room), { method: "GET", cache: "no-store" })
      .then(function (r) {
        if (r.status === 404) {
          var missing = new Error("missing");
          missing.missing = true;
          throw missing;
        }
        if (!r.ok) throw new Error("HTTP " + r.status);
        return r.text();
      })
      .then(function (text) {
        var parsed = parseJsonl(text);
        render(doc, parsed);
        if (!String(text || "").trim()) note.textContent = noteFor("empty", room);
        else if (parsed.empty) note.textContent = noteFor("unreadable", room, parsed.skipped);
        else if (parsed.skipped) note.textContent = noteFor("skipped", room, parsed.skipped);
        else note.textContent = noteFor("ready", room);
      })
      .catch(function (e) {
        render(doc, parseJsonl(""));
        if (e && e.missing) note.textContent = noteFor("missing", room);
        else note.textContent = noteFor("error", room, e && e.message ? e.message : "error");
      });
  }

  if (typeof document !== "undefined" && document.getElementById && document.getElementById("board-panel")) {
    if (document.readyState === "loading")
      document.addEventListener("DOMContentLoaded", function () { mount(document); });
    else mount(document);
  }

  return {
    DEFAULT_ROOM: DEFAULT_ROOM,
    safeRoom: safeRoom,
    roomFromSearch: roomFromSearch,
    boardUrl: boardUrl,
    boardPath: boardPath,
    parseJsonl: parseJsonl,
    viewModel: viewModel,
    noteFor: noteFor,
    formatTs: formatTs,
    render: render,
    mount: mount
  };
});
