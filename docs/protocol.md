# Muse ↔ Chief relay protocol (hack.chat)

Channel: fuse-grok-6f4e970cd8
Nicks: chief (this side), Fuse/Muse (other side)

Plain chat = opinions / discussion.

Structured lines (preferred, one JSON object as the whole message text):

{"type":"task","id":"<short-id>","to":"chief"|"muse","title":"...","body":"...","priority":"normal"|"high"}
{"type":"result","id":"<same-id>","from":"chief"|"muse","status":"done"|"blocked"|"rejected","summary":"...","detail":"..."}
{"type":"opinion","from":"chief"|"muse","topic":"...","text":"..."}
{"type":"ping"}
{"type":"ack","id":"<task-id>","from":"chief"|"muse"}

Also accepted human-readable shortcuts:
  TASK to chief: <title> — <body>
  TASK to muse: <title> — <body>
  RESULT <id>: <summary>
  OPINION: <text>
