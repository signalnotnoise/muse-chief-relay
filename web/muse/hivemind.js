// Pure HIVEMIND helpers. No SDK, no network, no env reads.
// Board identity is the sha256 hex of the trimmed channel. Callers pass that
// hash. This module never accepts a channel name.
"use strict";

const { createHash } = require("node:crypto");
const { isDeepStrictEqual } = require("node:util");

const HASH_RE = /^[a-f0-9]{64}$/;
const SLUG_RE = /^[a-z0-9]+(?:-[a-z0-9]+)*$/;
const TASK_STATES = { open: 1, claimed: 1, blocked: 1, done: 1 };
const KINDS = { task: 1, decision: 1, scratch: 1 };
const AUTHORS = { chief: 1, fuse: 1, alex: 1 };

const string = (size, required, array) => ({ type: "string", size: size, required: !!required, array: !!array });
const integer = (required) => ({ type: "integer", required: !!required, array: false });

// Attribute sizes match the live hivemind collections. Indexes are not copied;
// reads use boardKey/seq, slug, and the knowledge fulltext index.
const SCHEMA = {
  boards: {
    key: string(64, true),
    label: string(128, false),
    createdTs: integer(true),
  },
  cards: {
    boardKey: string(64, true),
    kind: string(16, true),
    seq: integer(true),
    cardId: integer(false),
    title: string(256, false),
    owner: string(64, false),
    state: string(16, false),
    blockedOn: string(256, false),
    handoffTo: string(64, false),
    decider: string(64, false),
    decision: string(1024, false),
    context: string(2048, false),
    author: string(64, false),
    text: string(8192, false),
    ts: integer(false),
  },
  knowledge: {
    slug: string(128, true),
    title: string(256, true),
    summary: string(240, true),
    tags: string(32, true, true),
    source: string(512, true),
    authors: string(32, true, true),
    created: string(10, true),
    supersedes: string(128, false, true),
    links: string(128, false, true),
    visibility: string(16, true),
    flagged: string(16, false),
    body: string(65535, true),
  },
};

function own(object, key) {
  return Object.prototype.hasOwnProperty.call(object, key);
}

function isObject(value) {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

function documentId(collectionId, identity) {
  const digest = createHash("sha256").update(String(collectionId) + "\0" + String(identity)).digest("hex");
  return "s1_" + digest.slice(0, 32);
}

function emptyBoard() {
  return { tasks: [], decisions: [], scratch: [], skipped: 0 };
}

function validateShape(collectionId, data) {
  const attrs = SCHEMA[collectionId];
  if (!attrs || !isObject(data)) return "type";
  const keys = Object.keys(data);
  for (let i = 0; i < keys.length; i++) {
    if (!own(attrs, keys[i])) return "unknown";
  }
  const names = Object.keys(attrs);
  for (let i = 0; i < names.length; i++) {
    const name = names[i];
    const attr = attrs[name];
    if (!own(data, name)) {
      if (attr.required) return "missing";
      continue;
    }
    const value = data[name];
    if (attr.array) {
      if (!Array.isArray(value)) return "type";
      for (let j = 0; j < value.length; j++) {
        if (typeof value[j] !== "string" || value[j].length > attr.size) return "length";
      }
      continue;
    }
    if (attr.type === "string") {
      if (typeof value !== "string" || value.length > attr.size) return "length";
    } else if (!Number.isSafeInteger(value)) {
      return "type";
    }
  }
  return "";
}

function validateDocument(collectionId, data) {
  const shape = validateShape(collectionId, data);
  if (shape) return shape;
  if (collectionId === "boards") {
    if (!HASH_RE.test(data.key)) return "key";
    if (data.createdTs < 0) return "type";
    return "";
  }
  if (collectionId === "cards") return validateCard(data);
  if (collectionId === "knowledge") return validateNote(data);
  return "unknown";
}

function validateCard(data) {
  if (!HASH_RE.test(data.boardKey) || !KINDS[data.kind]) return "enum";
  if (!Number.isSafeInteger(data.seq) || data.seq < 1) return "type";
  if (data.kind === "task") {
    if (!own(data, "cardId") || !own(data, "title") || !own(data, "owner") || !own(data, "state")) return "missing";
    if (!Number.isSafeInteger(data.cardId) || data.cardId < 1) return "type";
    if (!TASK_STATES[data.state]) return "enum";
    return "";
  }
  if (!own(data, "ts") || data.ts < 0) return "type";
  if (data.kind === "decision") {
    if (!own(data, "decider") || !own(data, "decision")) return "missing";
    return "";
  }
  if (!own(data, "author") || !own(data, "text")) return "missing";
  return "";
}

function validDate(value) {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(value)) return false;
  const date = new Date(value + "T00:00:00Z");
  return Number.isFinite(date.getTime()) && date.toISOString().slice(0, 10) === value;
}

function validateNote(data) {
  if (!SLUG_RE.test(data.slug) || data.slug.length > 80) return "key";
  if (data.visibility !== "public") return "visibility";
  if (!data.title.trim() || !data.summary.trim() || !data.body.trim()) return "missing";
  if (!validDate(data.created)) return "type";
  if (!data.tags.length || !data.authors.length) return "missing";
  for (let i = 0; i < data.tags.length; i++) {
    if (!SLUG_RE.test(data.tags[i])) return "enum";
  }
  for (let i = 0; i < data.authors.length; i++) {
    if (!AUTHORS[data.authors[i]]) return "enum";
  }
  if (own(data, "flagged") && data.flagged !== "important") return "enum";
  const source = data.source.trim();
  if (source === "alex" || source === "room") {
    if (data.source !== source) return "enum";
  } else {
    let url;
    try { url = new URL(source); }
    catch (e) { return "enum"; }
    if (url.protocol !== "https:" || url.username || url.password) return "enum";
  }
  for (const key of ["supersedes", "links"]) {
    if (!own(data, key)) continue;
    const seen = new Set();
    for (let i = 0; i < data[key].length; i++) {
      const id = data[key][i];
      if (!SLUG_RE.test(id) || id.length > 80 || id === data.slug || seen.has(id)) return "enum";
      seen.add(id);
    }
  }
  return "";
}

// Appwrite may return an omitted optional attribute as null, or [] for an
// array. Those match a plan that left the field out. A supplied value must
// match exactly. $metadata is ignored. Non-empty permissions are not a match.
function documentsMatch(collectionId, expected, existing) {
  const attrs = SCHEMA[collectionId];
  if (!attrs || !isObject(expected) || !isObject(existing)) return false;
  if (existing.$permissions && existing.$permissions.length) return false;
  const data = {};
  const keys = Object.keys(existing);
  for (let i = 0; i < keys.length; i++) {
    const key = keys[i];
    if (key.charAt(0) === "$") continue;
    const attr = attrs[key];
    const value = existing[key];
    if (!attr || attr.required || own(expected, key)) {
      data[key] = value;
      continue;
    }
    if (value === null) continue;
    if (attr.array && Array.isArray(value) && value.length === 0) continue;
    data[key] = value;
  }
  return isDeepStrictEqual(data, expected);
}

function cardDataFromLine(line, boardKey, seq) {
  if (!HASH_RE.test(boardKey) || !Number.isSafeInteger(seq) || seq < 1) return null;
  let obj;
  try { obj = JSON.parse(line); }
  catch (e) { return null; }
  if (!isObject(obj) || typeof obj.type !== "string") return null;
  const data = { boardKey: boardKey, kind: obj.type, seq: seq };
  if (obj.type === "task") {
    data.cardId = obj.id;
    data.title = obj.title;
    data.owner = obj.owner;
    data.state = obj.state;
    if (typeof obj.blocked_on === "string") data.blockedOn = obj.blocked_on;
    if (typeof obj.handoff_to === "string") data.handoffTo = obj.handoff_to;
  } else if (obj.type === "decision") {
    data.ts = obj.ts;
    data.decider = obj.decider;
    data.decision = obj.decision;
    if (typeof obj.context === "string") data.context = obj.context;
  } else if (obj.type === "scratch") {
    data.ts = obj.ts;
    data.author = obj.author;
    data.text = obj.text;
  } else {
    return null;
  }
  if (validateDocument("cards", data)) return null;
  return data;
}

function recordFromCard(doc) {
  if (!isObject(doc) || !KINDS[doc.kind]) return null;
  if (doc.kind === "task") {
    if (!Number.isSafeInteger(doc.cardId) || typeof doc.title !== "string" || typeof doc.owner !== "string" || !TASK_STATES[doc.state]) {
      return null;
    }
    const task = { type: "task", id: doc.cardId, title: doc.title, owner: doc.owner, state: doc.state };
    if (typeof doc.blockedOn === "string") task.blocked_on = doc.blockedOn;
    if (typeof doc.handoffTo === "string") task.handoff_to = doc.handoffTo;
    return task;
  }
  if (!Number.isSafeInteger(doc.ts)) return null;
  if (doc.kind === "decision") {
    if (typeof doc.decider !== "string" || typeof doc.decision !== "string") return null;
    const decision = { type: "decision", ts: doc.ts, decider: doc.decider, decision: doc.decision };
    if (typeof doc.context === "string") decision.context = doc.context;
    return decision;
  }
  if (typeof doc.author !== "string" || typeof doc.text !== "string") return null;
  return { type: "scratch", ts: doc.ts, author: doc.author, text: doc.text };
}

function cardsToBoard(docs) {
  const rows = Array.isArray(docs) ? docs.slice() : [];
  rows.sort((a, b) => {
    const as = a && Number.isSafeInteger(a.seq) ? a.seq : 0;
    const bs = b && Number.isSafeInteger(b.seq) ? b.seq : 0;
    return as - bs;
  });
  const tasks = [];
  const taskAt = new Map();
  const decisions = [];
  const scratch = [];
  let skipped = 0;
  for (let i = 0; i < rows.length; i++) {
    const rec = recordFromCard(rows[i]);
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

function noteFromDocument(doc) {
  if (!isObject(doc) || doc.visibility !== "public") return null;
  if (typeof doc.slug !== "string" || typeof doc.title !== "string" || typeof doc.summary !== "string" || typeof doc.body !== "string") {
    return null;
  }
  if (!Array.isArray(doc.tags) || !Array.isArray(doc.authors)) return null;
  const note = {
    slug: doc.slug,
    title: doc.title,
    summary: doc.summary,
    tags: doc.tags.slice(),
    source: doc.source,
    authors: doc.authors.slice(),
    created: doc.created,
    visibility: "public",
    body: doc.body,
  };
  if (Array.isArray(doc.supersedes)) note.supersedes = doc.supersedes.slice();
  if (Array.isArray(doc.links)) note.links = doc.links.slice();
  if (typeof doc.flagged === "string" && doc.flagged) note.flagged = doc.flagged;
  return note;
}

module.exports = {
  HASH_RE: HASH_RE,
  SCHEMA: SCHEMA,
  documentId: documentId,
  emptyBoard: emptyBoard,
  validateDocument: validateDocument,
  documentsMatch: documentsMatch,
  cardDataFromLine: cardDataFromLine,
  cardsToBoard: cardsToBoard,
  noteFromDocument: noteFromDocument,
};
