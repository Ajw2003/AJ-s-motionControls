# Session transcripts — latency audit (2026-07-31)

Raw Claude Code transcripts (JSONL, one message per line) from the audit session that
investigated phone→game input lag and produced the fix plan.

| File | What it is |
|---|---|
| `main-session.jsonl` | The main conversation (audit request → agent launches → verification), current as of export time |
| `subagent-explore-controller.jsonl` | Explore agent: phone controller web app (`controller/`) — sensors, send loop, throw gesture, WebSocket client |
| `subagent-explore-relay.jsonl` | Explore agent: Node relay (`relay/server.js`) — forwarding hot path, rooms, backpressure |
| `subagent-explore-unity.jsonl` | Explore agent: Unity game (`Assets/Scripts`) — RelayClient receive loop, AimPreview, throw handling |
| `subagent-plan-design.jsonl` | Plan agent: turned the audit findings into the phased implementation plan |

Source: `~\.claude\projects\C--Users-Ayden-s-mini-Desktop-GameDev-RockSkipping\`.
