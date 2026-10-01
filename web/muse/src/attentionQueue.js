// Attention queue: the room's hard blocks on Alex, derived from the room board.
//
// A board task in state "blocked" whose blocked_on / handoff_to / owner /
// title names Alex becomes one queue item: one line plus one CTA. Acting on
// the CTA clears the item for this browser session; it is gone for good once
// the task leaves the blocked state on the board. Agents surface a block by
// marking a task "blocked" with blocked_on (or handoff_to) naming Alex — the
// board file is the persistent store, so this module never touches browser
// storage (the client's no-storage guarantee holds).
//
// Pure and DOM-free: importable from node tests and from useChat.js.

const ALEX_RE = /\balex\b/i;
const MERGE_RE = /\bmerge\w*|\bprs?\b|pull requests?|\breviews?\b|\bapprov\w*/i;
// Bare "ship" is left out: "ship order" is a decision about ordering, not a
// deploy. Agents write deploy / release / shipped / shipping for a deploy go.
const DEPLOY_RE = /\bdeploy\w*|\breleases?\b|\bshipped\b|\bshipping\b/i;

export const ATTENTION_KINDS = ["merge", "deploy", "decision"];

const KIND_ORDER = { merge: 0, deploy: 1, decision: 2 };
const KIND_LABELS = { merge: "merge tap", deploy: "deploy go", decision: "decision" };
const KIND_CTA = { merge: "Open PRs", deploy: "Cleared", decision: "Cleared" };

// Stable identity for a board task, so a cleared item stays cleared while the
// task keeps blocking.
export function attentionKey(task) {
  return "task:" + task.id;
}

function namesAlex(task) {
  return [task.blocked_on, task.handoff_to, task.owner, task.title].some(
    (v) => typeof v === "string" && ALEX_RE.test(v),
  );
}

export function kindOfTask(task) {
  const text = [task.title, task.blocked_on].filter((v) => typeof v === "string").join(" ");
  if (MERGE_RE.test(text)) return "merge";
  if (DEPLOY_RE.test(text)) return "deploy";
  return "decision";
}

// One queue item per blocked-on-Alex task, sorted merge taps first, then
// deploy gos, then decisions, then by task id.
export function deriveAttentionItems(board) {
  const tasks = board && Array.isArray(board.tasks) ? board.tasks : [];
  const items = [];
  for (const task of tasks) {
    if (!task || task.type !== "task" || task.state !== "blocked") continue;
    if (!namesAlex(task)) continue;
    const kind = kindOfTask(task);
    const blockedOn = typeof task.blocked_on === "string" && task.blocked_on.trim()
      ? task.blocked_on.trim()
      : "";
    items.push({
      key: attentionKey(task),
      kind,
      kindLabel: KIND_LABELS[kind],
      title: String(task.title || ""),
      detail: blockedOn ? "waiting on " + blockedOn : "",
      ctaLabel: KIND_CTA[kind],
    });
  }
  items.sort((a, b) => {
    const byKind = KIND_ORDER[a.kind] - KIND_ORDER[b.kind];
    if (byKind !== 0) return byKind;
    const ai = parseInt(String(a.key).slice(5), 10);
    const bi = parseInt(String(b.key).slice(5), 10);
    if (Number.isFinite(ai) && Number.isFinite(bi)) return ai - bi;
    return a.key < b.key ? -1 : a.key > b.key ? 1 : 0;
  });
  return items;
}
