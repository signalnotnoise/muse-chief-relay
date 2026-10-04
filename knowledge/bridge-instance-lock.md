---
id: bridge-instance-lock
title: One bridge process holds the state lock
summary: "A second Chief.Bridge on the same host exits before it opens a socket. The lock does not cross hosts."
tags: [bridge, ops]
source: alex
authors: [chief]
created: 2026-10-03
links: [reply-lock-deadlock, reconnect-forever]
visibility: public
---

Alex asked for the guard in the bridge process. A local wrapper that refuses start and stop while connected is not in this repo, and it is not the boundary. Anyone can launch `dotnet Chief.Bridge.dll` or the apphost directly. A pid file can go stale or name the wrong process.

The run takes two non-blocking flocks before it opens a socket and before it opens writers under `base` (outbox, v2 ledgers, and a SQLite file kept in that directory):

- `{base}/bridge.instance.lock` — one owner of that state directory, held until the process exits. A crash releases it.
- `/tmp/chatbridge-identity/id-<sha256>.lock` — one owner on this host for the same endpoint, room, and nick, even when the state directories differ. The file name is a hash. The room, trip, token, and hook secret are not in the name or in the `already running`, `stop`, or `instance:` lines.

`start` exits 4 when an owner holds either lock. `status` does not open a socket and does not take the lock. `stop` and `restart` signal the kernel owner of the state lock, including while the socket is connected. Linux reads `/proc/locks`. When `statx` `stx_dev` disagrees with the device id that file prints, the mount id from `/proc/self/mountinfo` is used; a statx-only miss is not reported as a free lock. A readable `/proc/locks` that is incomplete, permission-denied, or a different device id is not a free lock either: a non-blocking probe decides whether this file is held. A pid is reported only when that device and inode matched, or when a live descriptor still refers to this file. A deleted or replaced descriptor is not the owner. They do not signal a pid that is only written in the lock file or in `state.json`, and they do not signal when the owner pid could not be verified. If the host identity lock is held from a different state directory, they leave that process alone and `restart` does not start another socket.

The flocks cover one host and one mount namespace. A second machine, or a container with a private `/tmp`, can still open a socket. The relay may then close the first connection with code 4000. One service owner per endpoint, room, and nick is still the rule across hosts.
