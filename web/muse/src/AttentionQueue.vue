<script setup>
// Attention queue: the room's hard blocks on Alex. Rendered only when the
// queue is non-empty. Each row is one line plus one CTA; the CTA is the act,
// and acting clears the item (the parent marks it cleared for the session).
// Merge taps link out to the repo's PR list; deploy gos and decisions clear
// with a tap.
const props = defineProps({
  items: { type: Array, required: true },
  // Repo PR list URL, from the app (kept out of the derivation module so the
  // room's repo stays a view-level constant).
  pullsUrl: { type: String, required: true },
});

defineEmits(["clear"]);

function chipClass(kind) {
  if (kind === "merge") return "border-accent/40 text-accent";
  if (kind === "deploy") return "border-warn/40 text-warn";
  return "border-accent-2/40 text-accent-2";
}
</script>

<template>
  <section
    id="attention-queue"
    aria-label="Needs Alex"
    class="mb-3 shrink-0 rounded-xl border border-line bg-panel-2 px-4 py-3"
  >
    <div class="mb-2 flex items-center gap-2">
      <p class="m-0 font-mono text-[0.72rem] tracking-[0.28em] text-accent">NEEDS ALEX</p>
      <span
        class="rounded-full bg-accent/15 px-1.5 py-px text-[0.68rem] font-bold text-accent"
      >{{ items.length }}</span>
    </div>
    <ul class="m-0 list-none p-0">
      <li
        v-for="item in items"
        :key="item.key"
        class="flex items-center gap-2.5 py-1.5 first:pt-0 last:pb-0 [&:not(:last-child)]:border-b [&:not(:last-child)]:border-line/60"
      >
        <span
          class="shrink-0 rounded-full border px-2 py-px text-[0.7rem] font-semibold tracking-wide uppercase"
          :class="chipClass(item.kind)"
        >{{ item.kindLabel }}</span>
        <span class="min-w-0 flex-1 truncate text-[0.88rem] text-ink" :title="item.title + (item.detail ? ' · ' + item.detail : '')">
          {{ item.title }}<span v-if="item.detail" class="text-muted"> · {{ item.detail }}</span>
        </span>
        <a
          v-if="item.kind === 'merge'"
          :href="pullsUrl"
          target="_blank"
          rel="noopener"
          class="shrink-0 cursor-pointer rounded-lg border border-btn-border bg-gradient-to-b from-btn-top to-btn-bottom px-3 py-1.5 text-[0.82rem] font-semibold text-ink transition hover:brightness-110 focus-visible:outline-solid focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
          @click="$emit('clear', item)"
        >{{ item.ctaLabel }} ↗</a>
        <button
          v-else
          type="button"
          class="shrink-0 cursor-pointer rounded-lg border border-btn-border bg-gradient-to-b from-btn-top to-btn-bottom px-3 py-1.5 text-[0.82rem] font-semibold text-ink transition hover:brightness-110 focus-visible:outline-solid focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
          @click="$emit('clear', item)"
        >{{ item.ctaLabel }}</button>
      </li>
    </ul>
  </section>
</template>
