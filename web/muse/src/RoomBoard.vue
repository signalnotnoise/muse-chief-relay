<script setup>
import { computed } from "vue";
import { formatBoardTs, skippedLineText } from "./roomBoard.js";

const props = defineProps({
  view: { type: Object, required: true },
});

defineEmits(["reload"]);

const tasks = computed(() => {
  const rows = props.view.board && props.view.board.tasks;
  if (!rows || !rows.length) return [];
  return rows.slice().sort((a, b) => a.id - b.id);
});

const skippedText = computed(() => {
  const n = props.view.board && props.view.board.skipped;
  if (!n) return "";
  return skippedLineText(n);
});

function taskMeta(task) {
  const bits = ["owner " + (task.owner || "—")];
  if (task.blocked_on) bits.push("blocked on " + task.blocked_on);
  if (task.handoff_to) bits.push("handoff to " + task.handoff_to);
  return bits.join(" · ");
}

function stateClass(state) {
  if (state === "open") return "text-accent";
  if (state === "claimed") return "text-warn";
  if (state === "blocked") return "text-danger";
  if (state === "done") return "text-accent-2";
  return "text-muted";
}
</script>

<template>
  <section
    id="room-board"
    class="flex max-h-[min(22rem,42dvh)] min-h-0 shrink flex-col overflow-hidden rounded-xl border border-line bg-panel-soft px-5 py-4 shadow-[0_10px_40px_rgba(0,0,0,0.25)]"
    aria-label="Room board"
  >
    <div class="flex shrink-0 items-center gap-2.5">
      <h2 class="m-0 flex-1 text-base font-semibold">Room board</h2>
      <span
        v-show="view.statusText"
        id="board-status"
        class="rounded-full border px-2 py-px text-[0.75rem]"
        :class="view.statusKind === 'ok' ? 'border-on-border text-accent-2' : view.statusKind === 'err' ? 'border-err-border text-danger' : 'border-line text-muted'"
      >{{ view.statusText }}</span>
      <button
        id="board-reload"
        type="button"
        class="cursor-pointer rounded-lg border border-line bg-transparent px-2.5 py-1 text-[0.8rem] font-medium text-muted disabled:cursor-not-allowed disabled:opacity-40"
        :disabled="view.reloadDisabled"
        @click="$emit('reload')"
      >Reload</button>
    </div>
    <p class="mt-2 mb-2 shrink-0 text-[0.82rem] text-muted">
      Read-only<span id="board-via">{{ view.viaText }}</span>. This page never writes the file.
    </p>
    <div id="board-body" class="min-h-0 flex-1 overflow-auto" aria-live="polite">
      <p v-if="!view.board" class="m-0 text-[0.82rem] text-muted">{{ view.message }}</p>
      <template v-else>
        <div class="grid grid-cols-1 gap-3 min-[821px]:grid-cols-[minmax(0,1.2fr)_minmax(0,1fr)_minmax(0,1fr)]">
          <div>
            <h3 class="m-0 mb-2 text-xs font-semibold tracking-[0.06em] text-muted uppercase">Tasks</h3>
            <p v-if="!tasks.length" class="m-0 text-[0.82rem] text-muted">None yet.</p>
            <article
              v-for="task in tasks"
              :key="task.id"
              class="mb-2 grid gap-1 rounded-lg border border-line bg-panel-2 px-2.5 py-2"
            >
              <div class="flex items-center justify-between gap-2">
                <span class="font-mono text-[0.78rem] text-muted">#{{ task.id }}</span>
                <span
                  class="rounded-full border border-line px-1.5 py-px text-[0.68rem] tracking-wide uppercase"
                  :class="stateClass(task.state)"
                >{{ task.state }}</span>
              </div>
              <div class="text-[0.92rem] leading-snug break-words">{{ task.title || "(untitled)" }}</div>
              <div class="text-[0.78rem] leading-snug text-muted break-words">{{ taskMeta(task) }}</div>
            </article>
          </div>
          <div>
            <h3 class="m-0 mb-2 text-xs font-semibold tracking-[0.06em] text-muted uppercase">Decisions</h3>
            <p v-if="!view.board.decisions.length" class="m-0 text-[0.82rem] text-muted">None yet.</p>
            <article
              v-for="(item, index) in view.board.decisions"
              :key="'d' + index"
              class="mb-2 grid gap-1 rounded-lg border border-line bg-panel-2 px-2.5 py-2"
            >
              <div class="text-[0.78rem] leading-snug text-muted break-words">{{ item.decider }} · {{ formatBoardTs(item.ts) }}</div>
              <div class="text-[0.92rem] leading-snug break-words">{{ item.decision }}</div>
              <div v-if="item.context" class="text-[0.78rem] leading-snug text-muted break-words">{{ item.context }}</div>
            </article>
          </div>
          <div>
            <h3 class="m-0 mb-2 text-xs font-semibold tracking-[0.06em] text-muted uppercase">Scratch</h3>
            <p v-if="!view.board.scratch.length" class="m-0 text-[0.82rem] text-muted">None yet.</p>
            <article
              v-for="(item, index) in view.board.scratch"
              :key="'s' + index"
              class="mb-2 grid gap-1 rounded-lg border border-line bg-panel-2 px-2.5 py-2"
            >
              <div class="text-[0.78rem] leading-snug text-muted break-words">{{ item.author }} · {{ formatBoardTs(item.ts) }}</div>
              <div class="text-[0.92rem] leading-snug break-words">{{ item.text }}</div>
            </article>
          </div>
        </div>
        <p v-if="skippedText" class="m-0 mt-2 text-[0.82rem] text-muted">{{ skippedText }}</p>
      </template>
    </div>
  </section>
</template>
