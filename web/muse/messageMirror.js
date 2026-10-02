// Async dual-write of an accepted room chat into HIVEMIND workspaces + messages.
// Default off. HIVEMIND_MESSAGE_MIRROR=1 enables writes. Unset or 0 does not
// call Appwrite, even when APPWRITE_* is set. Failures stay in this module:
// observeAcceptedChat resolves, and noteAcceptedChat does not throw.
"use strict";

const hive = require("./hivemind.js");

const DEFAULT_DELAYS = [50, 150];
const MAX_PENDING = 32;

function mirrorEnabled(env) {
  const source = env || {};
  const value = source.HIVEMIND_MESSAGE_MIRROR;
  return typeof value === "string" && value.trim() === "1";
}

function failCode(error) {
  if (error && Number.isInteger(error.code)) return String(error.code);
  if (error && (error.code === "timeout" || error.code === "limit")) return String(error.code);
  return "error";
}

function createMessageMirror(options) {
  const opts = options || {};
  const env = opts.env || process.env || {};
  const log = typeof opts.log === "function" ? opts.log : defaultLog;
  const delays = Array.isArray(opts.retryDelays) ? opts.retryDelays : DEFAULT_DELAYS;
  const maxPending = Number.isSafeInteger(opts.maxPending) && opts.maxPending > 0 ? opts.maxPending : MAX_PENDING;
  const sleep = typeof opts.sleep === "function" ? opts.sleep : (ms) => new Promise((resolve) => setTimeout(resolve, ms));
  let client = opts.hivemind || null;
  let opening = null;
  const queue = [];
  let depth = 0;
  let pumping = false;

  function report(operation, error) {
    try { log("hivemind " + operation + " failed (" + failCode(error) + ")"); }
    catch (e) { /* a broken logger must not reject the chat path */ }
  }

  function refuse(reason) {
    try { log("hivemind message mirror refused (" + reason + ")"); }
    catch (e) { /* ignore */ }
  }

  function store() {
    if (client) return Promise.resolve(client);
    if (opening) return opening;
    opening = Promise.resolve().then(() => {
      const mod = require("./hivemindClient.js");
      client = mod.createHivemind({ env: env, log: log, timeoutMs: opts.timeoutMs });
      return client;
    }).catch((error) => {
      opening = null;
      report("message mirror", error);
      return null;
    });
    return opening;
  }

  function terminal(status) {
    return status === "created" || status === "verified" || status === "conflict" || status === "unconfigured";
  }

  async function writePlan(plan) {
    const api = await store();
    if (!api) return { status: "error" };
    let workspace;
    try {
      workspace = await api.createOrVerifyWorkspace(plan.workspace);
    } catch (error) {
      report("workspaces create-or-verify", error);
      return { status: "error" };
    }
    if (!workspace || workspace.status === "error" || workspace.status === "unconfigured") {
      return workspace || { status: "error" };
    }
    if (!terminal(workspace.status)) return { status: "error" };
    try {
      const message = await api.createOrVerifyMessage(plan.message, plan.documentId);
      return message || { status: "error" };
    } catch (error) {
      report("messages create-or-verify", error);
      return { status: "error" };
    }
  }

  async function writeWithRetry(plan) {
    let last = { status: "error" };
    const attempts = delays.length + 1;
    for (let i = 0; i < attempts; i++) {
      last = await writePlan(plan);
      if (!last || terminal(last.status)) return last || { status: "error" };
      if (i < delays.length) {
        try { await sleep(delays[i]); }
        catch (error) { report("message mirror", error); }
      }
    }
    return last;
  }

  function pump() {
    if (pumping) return;
    pumping = true;
    const step = () => {
      const job = queue.shift();
      if (!job) {
        pumping = false;
        return;
      }
      writeWithRetry(job.plan).then((result) => {
        depth -= 1;
        try { job.resolve(result || { status: "error" }); }
        catch (e) { /* the caller already left */ }
        step();
      }, () => {
        depth -= 1;
        try { job.resolve({ status: "error" }); }
        catch (e) { /* ignore */ }
        step();
      });
    };
    step();
  }

  function observeAcceptedChat(input) {
    if (!mirrorEnabled(env)) return Promise.resolve({ status: "disabled" });
    let plan;
    try {
      plan = hive.planRoomMirror(input);
    } catch (error) {
      report("message mirror", error);
      return Promise.resolve({ status: "error" });
    }
    if (!plan.ok) {
      refuse(plan.reason || "type");
      return Promise.resolve({ status: "error" });
    }
    if (depth >= maxPending) {
      refuse("queue");
      return Promise.resolve({ status: "error" });
    }
    depth += 1;
    return new Promise((resolve) => {
      queue.push({ plan: plan, resolve: resolve });
      pump();
    });
  }

  return {
    enabled: () => mirrorEnabled(env),
    observeAcceptedChat: observeAcceptedChat,
  };
}

// Fire-and-forget boundary. A throw or a rejected mirror promise does not escape.
function noteAcceptedChat(mirror, input) {
  try {
    if (!mirror || typeof mirror.observeAcceptedChat !== "function") return;
    const pending = mirror.observeAcceptedChat(input);
    if (pending && typeof pending.then === "function") {
      pending.then(() => {}, () => {});
    }
  } catch (e) { /* chat delivery already happened */ }
}

function defaultLog(message) {
  try { console.error(message); }
  catch (e) { /* stderr can be closed */ }
}

function readStdin() {
  return new Promise((resolve, reject) => {
    const chunks = [];
    process.stdin.on("data", (chunk) => chunks.push(chunk));
    process.stdin.on("end", () => resolve(Buffer.concat(chunks).toString("utf8")));
    process.stdin.on("error", reject);
  });
}

async function main() {
  const mirror = createMessageMirror({ env: process.env });
  if (!mirror.enabled()) return;
  let raw = "";
  try {
    raw = await readStdin();
  } catch (error) {
    defaultLog("hivemind message mirror failed (stdin)");
    return;
  }
  let input;
  try {
    input = JSON.parse(raw);
  } catch (error) {
    defaultLog("hivemind message mirror refused (json)");
    return;
  }
  try {
    await mirror.observeAcceptedChat(input);
  } catch (error) {
    defaultLog("hivemind message mirror failed (error)");
  }
}

if (require.main === module) {
  main().catch(() => {
    defaultLog("hivemind message mirror failed (error)");
  });
}

module.exports = {
  mirrorEnabled: mirrorEnabled,
  createMessageMirror: createMessageMirror,
  noteAcceptedChat: noteAcceptedChat,
};
