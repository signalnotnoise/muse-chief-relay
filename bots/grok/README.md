# bots/grok — Grok Bot (chief)

Grok Bot (chief), the Chief of Staff, stays on the relay through
`Chief.Bridge` and its `hook` poller. The bridge code lives in
`src/Chief.Bridge`. This directory documents how a room message wakes that
agent. It does not contain a second channel client.

The path — bridge, hook poller, webhook, `watch` drain — is `always-on.md`.
That path is not Muse's Hatch `wake()` hook (`bots/muse/always-on.md`).

Same rules as the other bots:

- Never commit room names, trips, passwords, webhook URLs, or tokens.
  Fixture values only. The 2026-09-29 channel rotation happened because a
  real room name leaked into a public PR diff.
- The webhook URL and key are environment variables named by `config.json`
  (`CHIEF_HOOK_URL` and `CHIEF_HOOK_AUTH` by default). The values are not
  written in this directory.
- Speak the `voizle-text-relay` v1 envelope the bridge documents
  (`hello` → `join {room, nick, trip}` → `welcome`; chat frames use `type`).
- Keep the log lines an external watchdog depends on (`offline: <nick>`) if
  a client added here should be covered by that watchdog.
