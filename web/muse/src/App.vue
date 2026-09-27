<script setup>
import RoomBoard from "./RoomBoard.vue";
import { useChat } from "./useChat.js";

const field =
  "rounded-lg border border-line bg-panel-2 px-2.5 py-[0.55rem] text-ink outline-none focus:border-focus focus:shadow-[0_0_0_3px_var(--color-focus-ring)] aria-invalid:border-danger";
const button =
  "cursor-pointer rounded-lg border border-btn-border bg-gradient-to-b from-btn-top to-btn-bottom px-3.5 py-[0.55rem] font-semibold text-ink hover:brightness-110";

const {
  statusText,
  statusKind,
  inChat,
  channel,
  channelError,
  nick,
  message,
  messages,
  users,
  metaChannel,
  metaNick,
  metaTrip,
  taskTitle,
  taskBody,
  taskRepo,
  opinionTopic,
  opinionText,
  resultId,
  resultStatus,
  resultSummary,
  channelEl,
  nickEl,
  passwordEl,
  transcriptEl,
  messageEl,
  boardView,
  reloadBoard,
  onTranscriptScroll,
  onNickInput,
  onChannelInput,
  onMessageInput,
  onJoin,
  disconnect,
  onSend,
  onTask,
  onOpinion,
  onResult,
} = useChat();
</script>

<template>
  <div
    class="mx-auto flex h-full min-h-0 max-w-[1100px] flex-col gap-3.5 px-[1.1rem] pt-4 pb-6"
    :class="inChat ? 'overflow-hidden' : 'overflow-auto'"
  >
    <header class="flex shrink-0 items-center justify-between">
      <div class="flex items-baseline gap-2">
        <span class="text-xl font-bold tracking-wide">Muse</span>
        <span class="text-[0.9rem] text-muted">↔ Chief relay</span>
      </div>
      <div
        id="status"
        class="rounded-full border bg-panel px-2.5 py-1 text-[0.8rem]"
        :class="statusKind === 'on' ? 'border-on-border text-accent-2' : statusKind === 'err' ? 'border-err-border text-danger' : 'border-line text-muted'"
      >{{ statusText }}</div>
    </header>

    <RoomBoard :view="boardView" @reload="reloadBoard" />

    <section
      id="join-panel"
      class="shrink-0 rounded-xl border border-line bg-panel-soft px-5 py-4 shadow-[0_10px_40px_rgba(0,0,0,0.25)]"
      :class="{ hidden: inChat }"
    >
      <h1 class="mb-4 text-[1.15rem] font-semibold">Join channel</h1>
      <form id="join-form" class="grid max-w-[420px] gap-3" @submit.prevent="onJoin">
        <label class="grid gap-1.5 text-[0.85rem] text-muted">
          Channel
          <input
            id="channel"
            ref="channelEl"
            :class="field"
            type="text"
            :value="channel"
            placeholder="your-channel-name"
            autocomplete="off"
            spellcheck="false"
            aria-required="true"
            aria-describedby="channel-error"
            :aria-invalid="channelError ? 'true' : undefined"
            @input="onChannelInput"
          />
        </label>
        <p id="channel-error" class="-mt-1.5 text-[0.8rem] leading-snug text-danger" :class="{ hidden: !channelError }" role="alert">
          Enter a channel name. Use the same one as Chief (the <code class="rounded bg-panel-2 px-1 py-px font-mono text-[0.78rem]">channel</code> in its <code class="rounded bg-panel-2 px-1 py-px font-mono text-[0.78rem]">config.json</code>).
        </p>
        <label class="grid gap-1.5 text-[0.85rem] text-muted">
          Nick
          <input
            id="nick"
            ref="nickEl"
            :class="field"
            type="text"
            :value="nick"
            autocomplete="off"
            spellcheck="false"
            @input="onNickInput"
          />
        </label>
        <!-- No name attribute on purpose: the password must not be able to land in a query string. -->
        <label class="grid gap-1.5 text-[0.85rem] text-muted">
          Password (optional, for a trip)
          <input
            id="password"
            ref="passwordEl"
            :class="field"
            type="password"
            autocomplete="current-password"
            spellcheck="false"
            aria-describedby="password-hint"
          />
        </label>
        <p id="password-hint" class="-mt-1.5 text-[0.78rem] leading-snug text-muted">
          Gives your nick a tripcode so others can tell it's really you. Sent to hack.chat only in the join, never shown, logged or saved by this page. hack.chat ignores anything after a <code class="rounded bg-panel-2 px-1 py-px font-mono text-[0.78rem]">#</code> in the password.
        </p>
        <button :class="button" type="submit">Connect</button>
      </form>
      <p class="mt-4 text-[0.82rem] text-muted">
        Uses <code class="rounded bg-panel-2 px-1 py-px font-mono text-[0.78rem]">wss://hack.chat/chat-ws</code>. Serve the built page from any static host, or run the Vite dev server.
      </p>
    </section>

    <section
      id="chat-panel"
      class="grid min-h-0 flex-1 grid-cols-1 grid-rows-[minmax(0,10rem)_minmax(0,1fr)] overflow-hidden rounded-xl border border-line bg-panel-soft p-0 shadow-[0_10px_40px_rgba(0,0,0,0.25)] min-[821px]:grid-cols-[200px_minmax(0,1fr)] min-[821px]:grid-rows-[minmax(0,1fr)]"
      :class="{ hidden: !inChat }"
    >
      <aside class="flex min-h-0 min-w-0 flex-col gap-2.5 overflow-hidden border-b border-line bg-sidebar px-3.5 py-3.5 max-[820px]:max-h-40 min-[821px]:border-r min-[821px]:border-b-0">
        <h2 class="m-0 shrink-0 text-xs font-semibold tracking-[0.06em] text-muted uppercase">Online</h2>
        <ul id="users" class="m-0 min-h-0 flex-1 list-none overflow-auto p-0 text-[0.9rem]">
          <li
            v-for="name in users"
            :key="name"
            class="rounded-md px-1.5 py-1 text-chat"
            :class="{ 'bg-me text-accent': name === nick }"
          >{{ name }}</li>
        </ul>
        <div class="grid shrink-0 gap-1 text-[0.78rem] text-muted">
          <div><span class="text-dim">channel</span> <span id="meta-channel" class="font-mono text-[0.74rem] text-ink">{{ metaChannel }}</span></div>
          <div><span class="text-dim">you</span> <span id="meta-nick" class="font-mono text-[0.74rem] text-ink">{{ metaNick }}</span></div>
          <div><span class="text-dim">trip</span> <span id="meta-trip" class="font-mono text-[0.74rem] text-ink">{{ metaTrip }}</span></div>
        </div>
        <button id="disconnect" type="button" class="shrink-0 cursor-pointer rounded-lg border border-line bg-transparent px-3.5 py-2 font-medium text-muted" @click="disconnect">Disconnect</button>
      </aside>

      <main class="flex min-h-0 min-w-0 flex-col overflow-hidden">
        <div
          id="transcript"
          ref="transcriptEl"
          class="flex min-h-0 flex-1 flex-col gap-1.5 overflow-y-auto overscroll-contain px-4 py-3.5"
          aria-live="polite"
          @scroll="onTranscriptScroll"
        >
          <div
            v-for="row in messages"
            :key="row.id"
            class="grid gap-0.5 rounded-lg px-2 py-1.5 hover:bg-row-hover"
            :class="{ 'border border-proto-border bg-proto-bg': row.kind === 'proto' }"
          >
            <div class="flex items-baseline gap-2 text-[0.75rem] text-muted">
              <span v-if="row.nick" class="font-semibold" :class="row.me ? 'text-accent-2' : 'text-accent'">{{ row.nick }}</span>
              <span class="font-mono text-[0.7rem] text-time">{{ row.time }}</span>
            </div>
            <div v-if="row.tag" class="mb-1 inline-block w-fit rounded-full border border-proto-tag px-[0.45rem] py-px text-[0.68rem] tracking-wider text-proto uppercase">{{ row.tag }}</div>
            <div
              class="whitespace-pre-wrap break-words leading-snug"
              :class="row.kind === 'sys' ? 'text-[0.85rem] text-sys' : row.kind === 'proto' ? 'font-mono text-[0.8rem] text-proto' : 'text-chat'"
            >{{ row.text }}</div>
          </div>
        </div>

        <details class="max-h-[42%] min-h-0 shrink overflow-auto border-t border-line bg-actions px-4 pt-2 pb-3.5">
          <summary class="cursor-pointer text-[0.85rem] text-muted select-none">Quick protocol actions</summary>
          <div class="mt-3 grid grid-cols-1 gap-3 min-[821px]:grid-cols-3">
            <form id="task-form" class="grid gap-1.5 rounded-lg border border-line bg-panel p-2.5" @submit.prevent="onTask">
              <h3 class="m-0 mb-0.5 text-[0.8rem] font-semibold text-muted">Task → chief</h3>
              <input :class="field" v-model="taskTitle" placeholder="Title" required />
              <textarea :class="field" v-model="taskBody" placeholder="Body" rows="2" required></textarea>
              <input
                :class="field"
                v-model="taskRepo"
                placeholder="Repo (optional, owner/name; public status only if listed)"
                autocomplete="off"
                spellcheck="false"
              />
              <button :class="[button, 'px-2.5 py-1.5 text-[0.85rem]']" type="submit">Send task</button>
            </form>
            <form id="opinion-form" class="grid gap-1.5 rounded-lg border border-line bg-panel p-2.5" @submit.prevent="onOpinion">
              <h3 class="m-0 mb-0.5 text-[0.8rem] font-semibold text-muted">Opinion</h3>
              <input :class="field" v-model="opinionTopic" placeholder="Topic" />
              <textarea :class="field" v-model="opinionText" placeholder="Your take" rows="2" required></textarea>
              <button :class="[button, 'px-2.5 py-1.5 text-[0.85rem]']" type="submit">Send opinion</button>
            </form>
            <form id="result-form" class="grid gap-1.5 rounded-lg border border-line bg-panel p-2.5" @submit.prevent="onResult">
              <h3 class="m-0 mb-0.5 text-[0.8rem] font-semibold text-muted">Result</h3>
              <input :class="field" v-model="resultId" placeholder="Task id" required />
              <select :class="field" v-model="resultStatus">
                <option value="done">done</option>
                <option value="blocked">blocked</option>
                <option value="rejected">rejected</option>
              </select>
              <textarea :class="field" v-model="resultSummary" placeholder="Summary" rows="2" required></textarea>
              <button :class="[button, 'px-2.5 py-1.5 text-[0.85rem]']" type="submit">Send result</button>
            </form>
          </div>
        </details>

        <form id="send-form" class="flex shrink-0 gap-2 border-t border-line bg-panel px-4 py-3" @submit.prevent="onSend">
          <input
            id="message"
            ref="messageEl"
            :class="[field, 'min-w-0 flex-1']"
            type="text"
            :value="message"
            placeholder="Say something…"
            autocomplete="off"
            @input="onMessageInput"
          />
          <button :class="[button, 'shrink-0']" type="submit">Send</button>
        </form>
      </main>
    </section>
  </div>
</template>
