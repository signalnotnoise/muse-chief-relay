<script setup>
import { useChat } from "./useChat.js";

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
  <div class="app" :class="{ chatting: inChat }">
    <header class="top">
      <div class="brand">
        <span class="logo">Muse</span>
        <span class="sub">↔ Chief relay</span>
      </div>
      <div id="status" class="status" :class="statusKind">{{ statusText }}</div>
    </header>

    <section id="join-panel" class="panel join" :class="{ hidden: inChat }">
      <h1>Join channel</h1>
      <form id="join-form" @submit.prevent="onJoin">
        <label>
          Channel
          <input
            id="channel"
            ref="channelEl"
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
        <p id="channel-error" class="field-error" :class="{ hidden: !channelError }" role="alert">
          Enter a channel name. Use the same one as Chief (the <code>channel</code> in its <code>config.json</code>).
        </p>
        <label>
          Nick
          <input
            id="nick"
            ref="nickEl"
            type="text"
            :value="nick"
            autocomplete="off"
            spellcheck="false"
            @input="onNickInput"
          />
        </label>
        <!-- No name attribute on purpose: the password must not be able to land in a query string. -->
        <label>
          Password (optional, for a trip)
          <input
            id="password"
            ref="passwordEl"
            type="password"
            autocomplete="current-password"
            spellcheck="false"
            aria-describedby="password-hint"
          />
        </label>
        <p id="password-hint" class="field-hint">
          Gives your nick a tripcode so others can tell it's really you. Sent to hack.chat only in the join, never shown, logged or saved by this page. hack.chat ignores anything after a <code>#</code> in the password.
        </p>
        <button type="submit">Connect</button>
      </form>
      <p class="hint">Uses <code>wss://hack.chat/chat-ws</code>. Serve the built page from any static host, or run the Vite dev server.</p>
    </section>

    <section id="chat-panel" class="panel chat" :class="{ hidden: !inChat }">
      <aside class="sidebar">
        <h2>Online</h2>
        <ul id="users">
          <li v-for="name in users" :key="name" :class="{ me: name === nick }">{{ name }}</li>
        </ul>
        <div class="meta">
          <div><span class="k">channel</span> <span id="meta-channel">{{ metaChannel }}</span></div>
          <div><span class="k">you</span> <span id="meta-nick">{{ metaNick }}</span></div>
          <div><span class="k">trip</span> <span id="meta-trip">{{ metaTrip }}</span></div>
        </div>
        <button id="disconnect" type="button" class="ghost" @click="disconnect">Disconnect</button>
      </aside>

      <main class="main">
        <div
          id="transcript"
          ref="transcriptEl"
          class="transcript"
          aria-live="polite"
          @scroll="onTranscriptScroll"
        >
          <div
            v-for="row in messages"
            :key="row.id"
            class="row"
            :class="[row.kind, { me: row.me }]"
          >
            <div class="who">
              <span v-if="row.nick" class="nick">{{ row.nick }}</span>
              <span class="time">{{ row.time }}</span>
            </div>
            <div v-if="row.tag" class="tag">{{ row.tag }}</div>
            <div class="body">{{ row.text }}</div>
          </div>
        </div>

        <details class="actions">
          <summary>Quick protocol actions</summary>
          <div class="action-grid">
            <form id="task-form" class="action" @submit.prevent="onTask">
              <h3>Task → chief</h3>
              <input v-model="taskTitle" placeholder="Title" required />
              <textarea v-model="taskBody" placeholder="Body" rows="2" required></textarea>
              <input
                v-model="taskRepo"
                placeholder="Repo (optional, owner/name; public status only if listed)"
                autocomplete="off"
                spellcheck="false"
              />
              <button type="submit">Send task</button>
            </form>
            <form id="opinion-form" class="action" @submit.prevent="onOpinion">
              <h3>Opinion</h3>
              <input v-model="opinionTopic" placeholder="Topic" />
              <textarea v-model="opinionText" placeholder="Your take" rows="2" required></textarea>
              <button type="submit">Send opinion</button>
            </form>
            <form id="result-form" class="action" @submit.prevent="onResult">
              <h3>Result</h3>
              <input v-model="resultId" placeholder="Task id" required />
              <select v-model="resultStatus">
                <option value="done">done</option>
                <option value="blocked">blocked</option>
                <option value="rejected">rejected</option>
              </select>
              <textarea v-model="resultSummary" placeholder="Summary" rows="2" required></textarea>
              <button type="submit">Send result</button>
            </form>
          </div>
        </details>

        <form id="send-form" class="composer" @submit.prevent="onSend">
          <input
            id="message"
            ref="messageEl"
            type="text"
            :value="message"
            placeholder="Say something…"
            autocomplete="off"
            @input="onMessageInput"
          />
          <button type="submit">Send</button>
        </form>
      </main>
    </section>
  </div>
</template>
