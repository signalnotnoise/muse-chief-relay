// Read-only room board for the Muse page. Loaded after app.js.
// docs/muse and web/muse copies of this file are kept identical.
//
// The page fetches boards/fuse-grok-6f4e970cd8.jsonl and renders it.
// It never writes, and it never assumes it owns the last line.
(function (root, factory) {
  var api = factory();
  if (typeof module === "object" && module.exports) module.exports = api;
  if (typeof document !== "undefined") {
    if (document.readyState === "loading") {
      document.addEventListener("DOMContentLoaded", function () { api.mount(); });
    } else {
      api.mount();
    }
  }
})(typeof globalThis !== "undefined" ? globalThis : this, function () {
  // Deployed Muse is docs/muse/ on GitHub Pages (site root is docs/), so the
  // file next to that page is ../boards/<room>.jsonl. From either muse folder
  // when the server root is the repo, the same file is two levels up.
  var BOARD_URLS = [
    "../boards/fuse-grok-6f4e970cd8.jsonl",
    "../../boards/fuse-grok-6f4e970cd8.jsonl"
  ];

  var NOTES = {
    missing: "Room board isn't available from this page.",
    empty: "Room board is empty.",
    unreadable: "Room board couldn't be read.",
    partial: "Some lines couldn't be read."
  };

  var TASK_STATES = { open: true, claimed: true, blocked: true, done: true };

  function asText(v) {
    if (typeof v === "string") return v.trim();
    if (typeof v === "number" && isFinite(v)) return String(v);
    return "";
  }

  function parseBoard(text) {
    var records = [];
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
      if (!obj || typeof obj !== "object" || Array.isArray(obj)) {
        skipped++;
        continue;
      }
      if (obj.type !== "task" && obj.type !== "decision" && obj.type !== "scratch") {
        skipped++;
        continue;
      }
      records.push(obj);
    }
    return { records: records, skipped: skipped };
  }

  function viewBoard(text) {
    var parsed = parseBoard(text);
    var tasks = [];
    var decisions = [];
    var scratch = [];
    for (var i = 0; i < parsed.records.length; i++) {
      var rec = parsed.records[i];
      if (rec.type === "task") tasks.push(rec);
      else if (rec.type === "decision") decisions.push(rec);
      else scratch.push(rec);
    }
    var status = "ok";
    if (!parsed.records.length) status = parsed.skipped ? "unreadable" : "empty";
    return {
      status: status,
      skipped: parsed.skipped,
      tasks: tasks,
      decisions: decisions,
      scratch: scratch
    };
  }

  function noteFor(view) {
    if (!view || view.status === "empty") return NOTES.empty;
    if (view.status === "unreadable") return NOTES.unreadable;
    if (view.skipped) return NOTES.partial;
    return "";
  }

  function formatTs(ts) {
    if (typeof ts !== "number" || !isFinite(ts)) return asText(ts);
    var d = new Date(ts * 1000);
    if (isNaN(d.getTime())) return String(ts);
    try {
      return d.toLocaleString([], { dateStyle: "medium", timeStyle: "short" });
    } catch (e) {
      return d.toLocaleString();
    }
  }

  function idLabel(id) {
    if (typeof id === "number" && isFinite(id)) return "#" + id;
    if (typeof id === "string" && id.trim()) return "#" + id.trim();
    return "";
  }

  // Every value from the file is untrusted chat-side text. textContent only.
  function el(tag, cls, text) {
    var node = document.createElement(tag);
    if (cls) node.className = cls;
    if (text != null && text !== "") node.textContent = String(text);
    return node;
  }

  function setNote(mount, text) {
    var note = mount.querySelector("[data-board-note]");
    if (!note) return;
    note.textContent = text || "";
    note.classList.toggle("hidden", !text);
  }

  function addGroup(lists, title, cards) {
    if (!cards.length) return;
    var group = el("section", "board-group");
    group.appendChild(el("h3", null, title));
    var list = el("div", "board-list");
    for (var i = 0; i < cards.length; i++) list.appendChild(cards[i]);
    group.appendChild(list);
    lists.appendChild(group);
  }

  function taskCard(rec) {
    var card = el("article", "board-card");
    var line = el("div", "board-line");
    var id = idLabel(rec.id);
    var title = asText(rec.title) || "(untitled)";
    line.appendChild(el("div", "board-title", id ? id + "  " + title : title));
    var state = asText(rec.state);
    var pill = el("span", "pill" + (TASK_STATES[state] ? " " + state : ""), state || "?");
    line.appendChild(pill);
    card.appendChild(line);

    var bits = [];
    var owner = asText(rec.owner);
    var blocked = asText(rec.blocked_on);
    var handoff = asText(rec.handoff_to);
    if (owner) bits.push(owner);
    if (blocked) bits.push("blocked on " + blocked);
    if (handoff) bits.push("handoff to " + handoff);
    if (bits.length) card.appendChild(el("div", "board-meta", bits.join(" · ")));
    return card;
  }

  function decisionCard(rec) {
    var card = el("article", "board-card");
    card.appendChild(el("div", "board-title", asText(rec.decision) || "(no decision text)"));
    var bits = [];
    var who = asText(rec.decider);
    var when = formatTs(rec.ts);
    if (who) bits.push(who);
    if (when) bits.push(when);
    if (bits.length) card.appendChild(el("div", "board-meta", bits.join(" · ")));
    var context = asText(rec.context);
    if (context) card.appendChild(el("div", "board-context", context));
    return card;
  }

  function scratchCard(rec) {
    var card = el("article", "board-card");
    card.appendChild(el("div", "board-title", asText(rec.text) || "(empty)"));
    var bits = [];
    var who = asText(rec.author);
    var when = formatTs(rec.ts);
    if (who) bits.push(who);
    if (when) bits.push(when);
    if (bits.length) card.appendChild(el("div", "board-meta", bits.join(" · ")));
    return card;
  }

  function renderBoard(mount, view) {
    setNote(mount, noteFor(view));
    var lists = mount.querySelector("[data-board-lists]");
    if (!lists) return;
    while (lists.firstChild) lists.removeChild(lists.firstChild);
    if (!view || view.status !== "ok") return;
    var tasks = [];
    var decisions = [];
    var scratch = [];
    for (var i = 0; i < view.tasks.length; i++) tasks.push(taskCard(view.tasks[i]));
    for (var j = 0; j < view.decisions.length; j++) decisions.push(decisionCard(view.decisions[j]));
    for (var k = 0; k < view.scratch.length; k++) scratch.push(scratchCard(view.scratch[k]));
    addGroup(lists, "Tasks", tasks);
    addGroup(lists, "Decisions", decisions);
    addGroup(lists, "Scratch", scratch);
  }

  function headerType(res) {
    if (!res || !res.headers || typeof res.headers.get !== "function") return "";
    return res.headers.get("content-type") || "";
  }

  // First URL that returns the jsonl wins. An empty file is a real board
  // (status empty), not a miss, so we do not fall through to the next path.
  function fetchBoardText(fetchImpl) {
    var fetchFn = fetchImpl;
    function attempt(i) {
      if (i >= BOARD_URLS.length) return Promise.resolve({ status: "missing", text: "" });
      var url = BOARD_URLS[i];
      return Promise.resolve()
        .then(function () {
          return fetchFn(url, { cache: "no-store", credentials: "omit" });
        })
        .then(function (res) {
          if (!res || !res.ok || typeof res.text !== "function") return attempt(i + 1);
          if (/text\/html/i.test(headerType(res))) return attempt(i + 1);
          return res.text().then(function (text) {
            return { status: "ok", text: text };
          });
        })
        .catch(function () {
          return attempt(i + 1);
        });
    }
    return attempt(0);
  }

  function mount() {
    var mountEl = document.getElementById("room-board");
    if (!mountEl || typeof fetch !== "function") {
      if (mountEl) setNote(mountEl, NOTES.missing);
      return;
    }
    fetchBoardText(fetch.bind(globalThis)).then(function (got) {
      if (!got || got.status !== "ok") {
        setNote(mountEl, NOTES.missing);
        return;
      }
      try {
        renderBoard(mountEl, viewBoard(got.text));
      } catch (e) {
        setNote(mountEl, NOTES.unreadable);
      }
    }).catch(function () {
      setNote(mountEl, NOTES.missing);
    });
  }

  return {
    BOARD_URLS: BOARD_URLS,
    NOTES: NOTES,
    parseBoard: parseBoard,
    viewBoard: viewBoard,
    noteFor: noteFor,
    fetchBoardText: fetchBoardText,
    renderBoard: renderBoard,
    mount: mount
  };
});
