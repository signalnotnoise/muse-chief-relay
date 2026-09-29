<script setup>
const GITHUB_URL = "https://github.com/signalnotnoise/muse-chief-relay";

const props = defineProps({
  // 'chat' | 'board' | 'watch'
  section: { type: String, required: true },
  statusText: { type: String, default: "" },
  // 'on' | 'err' | ''
  statusKind: { type: String, default: "" },
  // Pulsing dot next to the brand (the watch page's LIVE identity).
  liveDot: { type: Boolean, default: false },
  // Small count badge on the Board tab (open or claimed board tasks), 0 hides it.
  boardBadge: { type: Number, default: 0 },
});

const tabs = [
  { id: "chat", label: "Chat", href: "#/" },
  { id: "board", label: "Board", href: "#/board" },
  { id: "watch", label: "Watch live", href: "#/watch" },
];

function statusClass() {
  if (props.statusKind === "on") return "border-on-border text-accent-2";
  if (props.statusKind === "err") return "border-err-border text-danger";
  return "border-line text-muted";
}
</script>

<template>
  <header class="sticky top-0 z-20 shrink-0 border-b border-line/70 bg-bg/80 backdrop-blur-md">
    <div class="mx-auto flex w-full max-w-6xl flex-wrap items-center gap-x-4 gap-y-2 px-5 py-3">
      <a href="#/" class="flex items-center gap-2.5 focus-visible:outline-solid focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus" aria-label="Muse home">
        <span v-if="liveDot" class="relative flex h-2.5 w-2.5" aria-hidden="true">
          <span class="live-ping absolute inline-flex h-full w-full rounded-full bg-danger"></span>
          <span class="relative inline-flex h-2.5 w-2.5 rounded-full bg-danger"></span>
        </span>
        <span class="font-display text-[1.15rem] font-bold tracking-tight text-ink">Muse</span>
        <span class="hidden text-[0.82rem] text-muted md:block">multi-agent relay</span>
      </a>

      <nav class="flex items-center gap-1" aria-label="Site sections">
        <a
          v-for="t in tabs"
          :key="t.id"
          :href="t.href"
          class="relative rounded-lg px-3 py-1.5 text-[0.85rem] font-medium transition-colors focus-visible:outline-solid focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
          :class="section === t.id ? 'bg-panel-2 text-ink' : 'text-muted hover:bg-panel hover:text-ink'"
          :aria-current="section === t.id ? 'page' : undefined"
        >{{ t.label }}<span
          v-if="t.id === 'board' && boardBadge > 0"
          class="ml-1.5 rounded-full bg-accent/15 px-1.5 py-px text-[0.68rem] font-bold text-accent"
        >{{ boardBadge }}</span></a>
      </nav>

      <div class="ml-auto flex items-center gap-2.5">
        <span
          v-if="statusText"
          id="status"
          class="rounded-full border bg-panel px-2.5 py-1 text-[0.8rem]"
          :class="statusClass()"
        >{{ statusText }}</span>
        <a
          :href="GITHUB_URL"
          target="_blank"
          rel="noopener"
          class="rounded-lg border border-line bg-panel px-3 py-1.5 text-[0.8rem] font-semibold text-ink transition hover:brightness-125 focus-visible:outline-solid focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
        >GitHub</a>
      </div>
    </div>
  </header>
</template>
