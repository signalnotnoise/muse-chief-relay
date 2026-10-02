// Node client for the live HIVEMIND Appwrite database.
// Reads prefer this client when APPWRITE_* is set. Writes are create-or-verify
// only: identical documents stay, different documents are not updated or deleted.
// Failures are logged as an operation and a status code, then returned. Callers
// fall back. The API key is never logged.
"use strict";

const hive = require("./hivemind.js");

const PAGE = 100;
const PAGE_CAP = 5000;

function nonempty(value) {
  return typeof value === "string" && value.trim() !== "";
}

function endpointOk(endpoint) {
  let url;
  try { url = new URL(endpoint); }
  catch (e) { return false; }
  return url.protocol === "https:" && !url.username && !url.password && !url.search && !url.hash;
}

function failCode(error) {
  if (error && Number.isInteger(error.code)) return String(error.code);
  if (error && (error.code === "timeout" || error.code === "limit")) return String(error.code);
  return "error";
}

function createHivemind(options) {
  const opts = options || {};
  const env = opts.env || process.env || {};
  const databaseId = nonempty(env.APPWRITE_DATABASE_ID) ? env.APPWRITE_DATABASE_ID.trim() : "hivemind";
  const log = typeof opts.log === "function" ? opts.log : defaultLog;
  const timeoutMs = Number.isSafeInteger(opts.timeoutMs) && opts.timeoutMs > 0 ? opts.timeoutMs : 8000;
  const pageSize = Number.isSafeInteger(opts.pageSize) && opts.pageSize > 0 ? opts.pageSize : PAGE;
  let databases = opts.databases || null;
  let opened = false;

  function envReady() {
    return nonempty(env.APPWRITE_ENDPOINT) && nonempty(env.APPWRITE_PROJECT_ID) && nonempty(env.APPWRITE_API_KEY) && endpointOk(env.APPWRITE_ENDPOINT.trim());
  }

  function isConfigured() {
    return !!(databases || envReady());
  }

  function report(operation, error) {
    try { log("hivemind " + operation + " failed (" + failCode(error) + ")"); }
    catch (e) { /* a broken logger must not stop the fallback */ }
  }

  function refuse(operation, reason) {
    try { log("hivemind " + operation + " refused (" + reason + ")"); }
    catch (e) { /* ignore */ }
  }

  function call(fn) {
    let timer;
    const timeout = new Promise((_, reject) => {
      timer = setTimeout(() => {
        const err = new Error("timeout");
        err.code = "timeout";
        reject(err);
      }, timeoutMs);
    });
    return Promise.race([Promise.resolve().then(fn), timeout]).finally(() => clearTimeout(timer));
  }

  function queryApi() {
    if (opts.query) return opts.query;
    return require("node-appwrite").Query;
  }

  function db() {
    if (databases) return databases;
    if (!envReady()) return null;
    if (opened) return null;
    try {
      const { Client, Databases } = require("node-appwrite");
      const client = new Client()
        .setEndpoint(env.APPWRITE_ENDPOINT.trim())
        .setProject(env.APPWRITE_PROJECT_ID.trim())
        .setKey(env.APPWRITE_API_KEY);
      databases = new Databases(client);
      opened = true;
      return databases;
    } catch (error) {
      opened = true;
      databases = null;
      report("client", error);
      return null;
    }
  }

  async function listAll(collectionId, filters) {
    const store = db();
    if (!store) {
      const err = new Error("unconfigured");
      err.code = "unconfigured";
      throw err;
    }
    const Query = queryApi();
    const docs = [];
    let offset = 0;
    for (;;) {
      const page = await call(() => store.listDocuments({
        databaseId: databaseId,
        collectionId: collectionId,
        queries: filters.concat([Query.limit(pageSize), Query.offset(offset)]),
        total: true,
        ttl: 0,
      }));
      const batch = page && Array.isArray(page.documents) ? page.documents : [];
      for (let i = 0; i < batch.length; i++) docs.push(batch[i]);
      offset += batch.length;
      const totalKnown = page && Number.isInteger(page.total);
      if (batch.length === 0 || batch.length < pageSize) break;
      if (totalKnown && docs.length >= page.total) break;
      if (offset > PAGE_CAP) {
        const err = new Error("limit");
        err.code = "limit";
        throw err;
      }
    }
    return docs;
  }

  async function getOne(collectionId, documentId) {
    const store = db();
    if (!store) {
      const err = new Error("unconfigured");
      err.code = "unconfigured";
      throw err;
    }
    return call(() => store.getDocument({
      databaseId: databaseId,
      collectionId: collectionId,
      documentId: documentId,
    }));
  }

  async function readBoard(boardKey) {
    if (!isConfigured()) return { status: "unconfigured", board: null, via: "" };
    if (!hive.HASH_RE.test(boardKey)) {
      refuse("cards read", "key");
      return { status: "error", board: null, via: "" };
    }
    try {
      const Query = queryApi();
      const docs = await listAll("cards", [
        Query.equal("boardKey", boardKey),
        Query.orderAsc("seq"),
      ]);
      if (docs.length === 0) {
        try {
          await getOne("boards", hive.documentId("boards", boardKey));
        } catch (error) {
          if (error && error.code === 404) return { status: "empty", board: null, via: "" };
          if (error && error.code === "unconfigured") return { status: "unconfigured", board: null, via: "" };
          report("boards read", error);
          return { status: "error", board: null, via: "" };
        }
        return { status: "ok", board: hive.emptyBoard(), via: "Appwrite" };
      }
      return { status: "ok", board: hive.cardsToBoard(docs), via: "Appwrite" };
    } catch (error) {
      if (error && error.code === "unconfigured") return { status: "unconfigured", board: null, via: "" };
      report("cards read", error);
      return { status: "error", board: null, via: "" };
    }
  }

  async function readKnowledge(slug) {
    if (!isConfigured()) return { status: "unconfigured", note: null };
    if (typeof slug !== "string" || !/^[a-z0-9]+(?:-[a-z0-9]+)*$/.test(slug) || slug.length > 128) {
      refuse("knowledge read", "key");
      return { status: "error", note: null };
    }
    try {
      const doc = await getOne("knowledge", hive.documentId("knowledge", slug));
      const note = hive.noteFromDocument(doc);
      if (!note) return { status: "empty", note: null };
      return { status: "ok", note: note };
    } catch (error) {
      if (error && error.code === 404) return { status: "empty", note: null };
      if (error && error.code === "unconfigured") return { status: "unconfigured", note: null };
      report("knowledge read", error);
      return { status: "error", note: null };
    }
  }

  async function searchKnowledge(term) {
    if (!isConfigured()) return { status: "unconfigured", notes: [] };
    if (typeof term !== "string" || !term.trim() || term.length > 256) {
      refuse("knowledge search", "key");
      return { status: "error", notes: [] };
    }
    try {
      const Query = queryApi();
      const docs = await listAll("knowledge", [Query.search("body", term)]);
      const notes = [];
      for (let i = 0; i < docs.length; i++) {
        const note = hive.noteFromDocument(docs[i]);
        if (note) notes.push(note);
      }
      return { status: "ok", notes: notes };
    } catch (error) {
      if (error && error.code === "unconfigured") return { status: "unconfigured", notes: [] };
      report("knowledge search", error);
      return { status: "error", notes: [] };
    }
  }

  async function createOrVerify(collectionId, identity, data, mode) {
    const operation = collectionId + " create-or-verify";
    const extra = mode || {};
    if (!isConfigured()) return { status: "unconfigured" };
    const reason = hive.validateDocument(collectionId, data);
    if (reason) {
      refuse(operation, reason);
      return { status: "error" };
    }
    const documentId = typeof extra.documentId === "string" && extra.documentId
      ? extra.documentId
      : hive.documentId(collectionId, identity);
    let existing = null;
    try {
      existing = await getOne(collectionId, documentId);
    } catch (error) {
      if (error && error.code === "unconfigured") return { status: "unconfigured" };
      if (!error || error.code !== 404) {
        report(operation, error);
        return { status: "error" };
      }
    }
    if (existing) {
      if (!hive.documentsMatch(collectionId, data, existing)) {
        // A message UUID that already exists is a duplicate chat. Leave the
        // stored document alone and report success so the mirror does not retry.
        if (extra.dedupConflict) return { status: "verified", documentId: documentId };
        refuse(operation, "conflict");
        return { status: "conflict", documentId: documentId };
      }
      return { status: "verified", documentId: documentId };
    }
    const store = db();
    if (!store) return { status: "unconfigured" };
    try {
      const created = await call(() => store.createDocument({
        databaseId: databaseId,
        collectionId: collectionId,
        documentId: documentId,
        data: data,
        permissions: [],
      }));
      if (!hive.documentsMatch(collectionId, data, created)) {
        refuse(operation, "conflict");
        return { status: "conflict", documentId: documentId };
      }
      return { status: "created", documentId: documentId };
    } catch (error) {
      if (error && error.code === 409) {
        // Message ids are the room UUID. A create conflict means that chat
        // is already stored. Do not update it, and do not treat the race as
        // a failure the mirror should retry.
        if (extra.dedupConflict) {
          return { status: "verified", documentId: documentId };
        }
        try {
          const raced = await getOne(collectionId, documentId);
          if (raced && hive.documentsMatch(collectionId, data, raced)) {
            return { status: "verified", documentId: documentId };
          }
        } catch (again) {
          report(operation, again);
          return { status: "error", documentId: documentId };
        }
        refuse(operation, "conflict");
        return { status: "conflict", documentId: documentId };
      }
      report(operation, error);
      return { status: "error", documentId: documentId };
    }
  }

  function createOrVerifyBoard(data) {
    if (!data || typeof data.key !== "string") {
      refuse("boards create-or-verify", "key");
      return Promise.resolve({ status: "error" });
    }
    return createOrVerify("boards", data.key, data);
  }

  function createOrVerifyCard(data) {
    if (!data || typeof data.boardKey !== "string" || !Number.isSafeInteger(data.seq)) {
      refuse("cards create-or-verify", "key");
      return Promise.resolve({ status: "error" });
    }
    return createOrVerify("cards", data.boardKey + ":" + data.seq, data);
  }

  function createOrVerifyNote(data) {
    if (!data || typeof data.slug !== "string") {
      refuse("knowledge create-or-verify", "key");
      return Promise.resolve({ status: "error" });
    }
    return createOrVerify("knowledge", data.slug, data);
  }

  function createOrVerifyWorkspace(data) {
    if (!data || !hive.isWorkspaceKey(data.key)) {
      refuse("workspaces create-or-verify", "key");
      return Promise.resolve({ status: "error" });
    }
    return createOrVerify("workspaces", data.key, data);
  }

  function createOrVerifyMessage(data, documentId) {
    const id = hive.messageDocumentId(documentId);
    if (!id) {
      refuse("messages create-or-verify", "id");
      return Promise.resolve({ status: "error" });
    }
    if (!data || typeof data.workspaceKey !== "string") {
      refuse("messages create-or-verify", "key");
      return Promise.resolve({ status: "error" });
    }
    return createOrVerify("messages", id, data, { documentId: id, dedupConflict: true });
  }

  async function readBoardOrGit(hash, gitOpts) {
    const extra = gitOpts || {};
    const hiveResult = await readBoard(hash);
    if (hiveResult.status === "ok" && hiveResult.board) {
      return { status: "ok", board: hiveResult.board, via: hiveResult.via || "Appwrite", origin: "appwrite" };
    }
    let boardApi = extra.board;
    if (!boardApi) {
      try { boardApi = require("./board.js"); }
      catch (error) {
        report("board fallback", error);
        return { status: "error", board: null, via: "", origin: "git" };
      }
    }
    let sources;
    try {
      sources = boardApi.boardSources(hash, extra.pageUrl || "", extra.cacheBust || 0);
    } catch (error) {
      refuse("board fallback", "key");
      return { status: "error", board: null, via: "", origin: "git" };
    }
    try {
      const result = await boardApi.loadBoardText(sources, extra.fetchImpl);
      if (result && result.status === "ok") {
        return {
          status: "ok",
          board: boardApi.parseBoard(result.text),
          via: result.via,
          origin: "git",
        };
      }
      return { status: result && result.status ? result.status : "error", board: null, via: "", origin: "git" };
    } catch (error) {
      report("board fallback", error);
      return { status: "error", board: null, via: "", origin: "git" };
    }
  }

  return {
    isConfigured: isConfigured,
    readBoard: readBoard,
    readKnowledge: readKnowledge,
    searchKnowledge: searchKnowledge,
    createOrVerifyBoard: createOrVerifyBoard,
    createOrVerifyCard: createOrVerifyCard,
    createOrVerifyNote: createOrVerifyNote,
    createOrVerifyWorkspace: createOrVerifyWorkspace,
    createOrVerifyMessage: createOrVerifyMessage,
    readBoardOrGit: readBoardOrGit,
  };
}

function defaultLog(message) {
  try { console.error(message); }
  catch (e) { /* stderr can be closed; the caller still gets the status */ }
}

module.exports = { createHivemind: createHivemind };
