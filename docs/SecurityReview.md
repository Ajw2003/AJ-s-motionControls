# Security review — pre-ship gap check

**Date:** 2026-09-11
**Scope:** the local-hosting architecture — relay (`relay/server.js`, `relay/dns.js`), controller
(`controller/js/app.js`), Unity networking (`Assets/Scripts/Net/`), and the installer
(`deploy/installer.iss`).

**Framing that matters before anything else:** this game carries no credentials, no personal data
beyond a chosen display name, and no payments. That downgrades most of what follows from
"dangerous" to "untidy" — and it is a property that must be *defended*: several items below assume
nothing worth stealing is ever added to this channel. If accounts, persistence, or payments are
ever added, everything in this document needs re-deriving from scratch.

A prior version of this review existed and was deleted from the repo (recoverable from git
history; a stale copy also still sits in `Assets/.claude/worktrees/stone-skipping-physics-34903d/docs/SecurityReview.md`,
a different branch's snapshot — do not treat that copy as current). This version was re-derived
against the code actually on this branch as of the date above, not copied from it.

---

## Already verified fixed — properties to preserve, not re-litigate

| Item | Where | Why it's solid |
|---|---|---|
| **Player-name HTML injection** | [controller/js/app.js](../controller/js/app.js) | Every render site uses `.textContent`; no `innerHTML`/`insertAdjacentHTML`/`document.write` remains in `controller/`. A name can never become markup. |
| **DNS open-resolver / amplification risk** | [relay/dns.js:239](../relay/dns.js:239), wired from [relay/server.js:895-908](../relay/server.js:895) | `createResponder` throws if `allow` (a CIDR) is not supplied — the resolver cannot start unguarded. Out-of-range queries are silently dropped, not refused (replying at all is the amplification behaviour). Rate-limited per source, default 50/s. |
| **Static-file path traversal** | [relay/server.js:393](../relay/server.js:393) | Guard is `resolved.startsWith(STATIC_DIR + path.sep)`, not a bare prefix test — closes the sibling-directory bug (`controller-evil/` passing a naive `startsWith(STATIC_DIR)` check). |
| **Room codes** | [relay/server.js:61,95](../relay/server.js:61) | `crypto.randomInt` over a 24-character unambiguous alphabet — not `Math.random()`, not sequential. |
| **Message/connection hygiene** | relay/server.js | 4096-byte message cap, 60 msg/s per-socket rate limit, 8-player room cap, `JSON.parse` in try/catch. |
| **Traffic encryption** | [relay/server.js:711-720](../relay/server.js:711) | Both legs (phone↔relay, relay↔Unity) run over real TLS (`https.createServer` backing the `WebSocketServer`, not just a `wss://` label) — required anyway for iOS motion-sensor secure-context rules, so it's structural, not optional. |
| **Uninstall cleanup** | [deploy/installer.iss:166](../deploy/installer.iss:166) | `[UninstallRun]` calls `setup-offline.ps1 -Uninstall`, which removes the firewall rules, the scheduled task, and the hosts entry — nothing left behind on a clean removal. |
| **Guest network isolation** | `deploy/setup-offline.ps1`, confirmed by [HotspotService.cs:232-239](../Assets/Scripts/Net/HotspotService.cs:232) | IP forwarding on the hotspot interface is disabled every session; `HotspotService` fails loudly if it can't confirm this rather than silently continuing. Guest traffic never exits via the host's IP. **Applies only when "Share internet" is off. Since 2026-10-02 sharing is the default: forwarding is left on and a guest reaches the internet and the home LAN, accepted by the host (`docs/6-decisions/Decisions.md`).** **Correction 2026-10-01:** until #70 the confirmation passed on a momentary reading that ICS reversed ~2 s later, so isolation was not actually holding; it now requires 6 s stable. |

The certificate's private key being intentionally public (see `docs/plans/magic-domain-replacement.md`)
is not a defect either — it's the mechanism that makes a trusted secure context possible without a
per-install CA. It authenticates *a page*, not *a host*; treat it as such and never build a login
on top of this channel.

---

## Open — check or fix before shipping to strangers

### 1. Code signing — nothing is signed

**Status: not done.** [deploy/installer.iss:43-46](../deploy/installer.iss:43) flags this in its own
header: an unsigned installer that requests admin, opens firewall holes, and unpacks an unsigned
`node.exe` is indistinguishable from malware to Windows SmartScreen and to AV heuristics — to both
the software and to a careful user.

**Why it matters most:** this is the difference between a stranger's first launch being a clean
install and being a "Windows protected your PC" wall that most people will not click through.

**Mitigation:** code-sign the installer, the bundled `node.exe`, and the Unity player executable
with an Authenticode certificate before any public distribution.

### 2. The installer is not a working artifact yet

**Status: written, never compiled.** Per its own STATUS comment, `deploy/installer.iss` has no
Unity build to point `SourceUnityBuild` at, no Node runtime staged, `npm install --production`
has not been run into a staged `relay/` copy, and the seed certificate has not been fetched via
`deploy/fetch-seed-cert.ps1`. It has never been run through ISCC.

**Mitigation:** before calling distribution ready, produce a real Windows Standalone build, stage
a Node runtime and pre-installed `relay/`, fetch the seed cert, compile with ISCC, and run it on a
clean VM with no Node installed — confirming a phone can join with zero manual steps (this is
already written down as the acceptance test in `docs/plans/magic-domain-replacement.md` Phase 5,
step 7 — it just hasn't been run).

### 3. No per-IP connection cap on the relay

**Status: absent.** Confirmed by grep — no `connectionsPerIp` / `maxConnections` logic exists in
`relay/server.js`. The rate limit is per-*socket* ([relay/server.js:58](../relay/server.js:58),
`RATE_MAX_MESSAGES`), so a single source opening many sockets sidesteps it entirely.

**Impact:** low — the worst case is one misbehaving device degrading its own party's relay, not a
cross-network attack, since the relay is only reachable from the host's own hotspot subnet.

**Mitigation:** cap concurrent connections per source address at the WebSocket upgrade handler.

### 4. No throttle on failed `JOIN_ROOM` attempts

**Status: absent.** Confirmed — no attempt counter or backoff around the `JOIN_ROOM` case
([relay/server.js:762](../relay/server.js:762)). Room-code space is 24⁴ ≈ 332,000, so brute-forcing
a live code is slow, and the prize is "join someone's party game" — low value — but there is
currently no friction at all on repeated wrong guesses from one socket.

**Mitigation:** cap failed `JOIN_ROOM` attempts per socket (e.g. disconnect after N wrong codes).

### 5. Hotspot credentials persist indefinitely — re-verify the assumption it leans on

**Status: deliberate design, not an oversight** — documented in
[HotspotCredentialsStore.cs:19-25](../Assets/Scripts/Net/HotspotCredentialsStore.cs:19). A returning
guest keeps the same Wi-Fi SSID/passphrase forever (so returning to a party doesn't mean a fresh
QR), and this is stated to be safe *only* because IP forwarding is disabled every session — a
returning guest can reach the relay and other phones, never the host's internet or LAN.
**Since 2026-10-02, with sharing on (the default), a returning guest gets internet and LAN access;
the host accepted this** (party guests already use the home Wi-Fi). The isolation claim holds only
for sharing-off sessions.

**What to check before shipping:** that the forwarding-disable step in item "Guest network
isolation" above genuinely fires on **every** code path that can start a hotspot session,
including crash-recovery and re-launch paths, not just the happy path. If that guarantee ever
silently breaks, this credential-persistence design breaks with it — a past guest would retain
open-ended network access rather than session-scoped access.

### 6. Player names may render as IMGUI rich text on the TV

**Status: unverified.** [ResultsScreen.cs:139-141](../Assets/Scripts/UI/ResultsScreen.cs:139) renders
`PlayerNames.Instance.DisplayNameFor(standing.PlayerId)` through `GUILayout.Label`. Unity's legacy
IMGUI treats rich-text tags (`<color=…>`, `<b>`, etc.) as live markup by default unless the
`GUIStyle` in use has `richText` explicitly disabled. A name like `<color=red>OWNED</color>` could
render styled rather than literal.

**Impact:** cosmetic at worst — IMGUI rich text has no scripting/code-execution surface — but
worth resolving rather than leaving unverified.

**Mitigation:** confirm whether the styles used at that call site (`_title`/`style`/etc.) have
`richText = false`; if not, either set it or escape `<`/`>` before display.

### 7. Bundled Node.js has no update path

**Status: open, structural.** Once a specific `node.exe` is pinned into the installer
(`deploy/installer.iss`'s `SourceNodeRuntime`), it stops receiving upstream security fixes the day
it ships, and there is currently no stated mechanism to push a newer runtime to existing installs.

**Mitigation:** pin a specific, current LTS version at packaging time, track its advisories, and
decide on an update path (e.g. a version check against a small manifest, prompting a re-install)
before this matters at scale. The attack surface is small — the relay only serves the local
hotspot subnet — but "small" is doing real work in that sentence, so it's worth keeping small
deliberately rather than by accident.

---

## Priority

| | Item | Status |
|---|---|---|
| 1 | Code-sign installer, `node.exe`, player exe (§1) | Open — gates whether a stranger's first launch is trustworthy |
| 2 | Finish and verify the installer end-to-end (§2) | Open — currently unbuildable |
| 3 | Per-IP connection cap (§3) | Open — cheap hardening |
| 4 | Failed-`JOIN_ROOM` throttle (§4) | Open — cheap hardening |
| 5 | Re-verify forwarding-disable fires on every code path (§5) | Open — applies to sharing-off sessions only; the assumption a deliberate design leans on |
| 6 | Confirm `richText` off for the results screen (§6) | Open — cosmetic, quick to check |
| 7 | Node runtime update path (§7) | Open — no urgency pre-launch, needed before this ships at scale |

Items under "Already verified fixed" above are properties to preserve, not defects to revisit —
the risk there is a future change quietly undoing one of them (e.g. a new render site for a player
name that reaches for `innerHTML` out of habit).
