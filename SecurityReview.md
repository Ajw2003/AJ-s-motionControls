# Security review — what the local-hosting design asks of a player

**Scope:** the hosting architecture in [ZeroSetupPlan.md](ZeroSetupPlan.md) — a bundled relay
listening on the player's PC, a borrowed wildcard certificate, a Windows hotspot, a local DNS
responder, and firewall rules opened at install time.

**Framing that matters before anything else:** this game carries **no credentials, no personal
data beyond a chosen display name, and no payments**. That single fact downgrades most of what
follows from "dangerous" to "untidy". It is also a property that must be *defended* — several
mitigations below are really the same instruction: do not add a feature that puts something
worth stealing on this channel.

---

## 1. The certificate authenticates nothing

**This is the most misunderstood part of the design, and it should be understood before it is
relied on.**

`local-ip.sh` publishes the private key for `*.local-ip.sh` **on purpose**. That is what makes
the whole approach possible — and it means the certificate provides:

- ✅ **A secure context**, which is the only reason phone motion sensors work at all
- ✅ **Forward secrecy against passive capture**, via ECDHE — someone recording traffic cannot
  decrypt it later merely by downloading the key
- ❌ **No authentication whatsoever.** Anyone can obtain the same key and serve a *valid,
  publicly trusted* certificate for any `*.local-ip.sh` name

So an attacker on the same network can transparently impersonate the game's page, and the phone
shows a padlock and **no warning of any kind**. The certificate is a compliance token that
unlocks an API, not a security control.

**What is actually exposed:** display names, aim vectors, throw descriptors, room codes, scores.
Nothing worth the effort.

**Mitigations**

- **Treat this channel as public.** Never add a login, a password field, a payment step, or any
  personal data to the controller page. This is a standing design rule, not a one-time check —
  it is the assumption every other item here leans on.
- **Never reuse this certificate approach for anything that matters.** If the project ever grows
  accounts or persistence, that traffic needs a real certificate, not a borrowed one.
- Document it in-product if the game is ever distributed: a host should know guests' phones are
  trusting the room, not the cryptography.

## 2. Guests hand the host complete control of their name resolution

When a phone joins the PC's hotspot, the PC becomes its DHCP server, its gateway **and its DNS
resolver**. The Phase 3.6 responder answers `*.local-ip.sh` and forwards everything else — which
means the host machine sees, and could redirect, *every* domain that phone looks up while
connected.

This is inherent to being someone's access point and is no different from joining any Wi-Fi
network. It is worth stating plainly because the game *creates* that relationship deliberately,
where normally a guest chooses it.

**Mitigations**

- **Forward verbatim; never rewrite, never log.** The responder must answer only the game's own
  name and pass everything else through untouched. Any temptation to "helpfully" intercept
  should be treated as a bug.
- Guests should be joining a host they already trust — true at a party, false for strangers.
- If the host machine is compromised, guests' browsing is compromised too. That raises the value
  of the code-signing item in §7.

## 3. Sharing the host's internet connection carries the host's liability

Windows tethering shares the uplink. Everything a guest does exits via **the host's IP address**
and is attributed to them. On a school or corporate network, sharing a connection this way also
commonly violates acceptable-use policy — which is worth noting given the hotspot's most
compelling use case is exactly those networks.

**Mitigations**

- Say so, once, where the host turns the hotspot on. Not a EULA — one line.
- Keep sessions short-lived; stop tethering when the game closes rather than leaving an open
  access point running indefinitely.

---

## 4. ✅ FIXED — the DNS responder was an open resolver

*Fixed 2026-08-20 in `deploy/spike/dns-responder.js`. Recorded in full because the reasoning
must survive into the shipped implementation, which is not written yet.*

**This was the most serious fixable issue, and the spike code had the bug.**

`deploy/spike/dns-responder.js` was run as:

```
--bind 0.0.0.0 --port 53 --upstream 1.1.1.1
```

Binding every interface *and* forwarding for anyone who asks is the textbook definition of an
**open recursive DNS resolver**. Consequences:

- Any host on any network the PC is attached to can use it as a free resolver
- It is a **DNS amplification reflector** — a small spoofed query returns a large answer to a
  forged source address, which is a standard DDoS building block
- The firewall rules from Phase 4 are what currently prevent this, which means one
  mis-scoped rule turns a player's PC into an attack tool

**What was applied**

- **Safe defaults.** `--bind` now defaults to `192.168.137.1`, not `0.0.0.0`, and `--allow`
  defaults to the bind address's `/24`. The safe configuration is the one you get by doing
  nothing, because the dangerous one is indistinguishable while you are testing it.
- **Source guard.** Queries from outside the permitted range are **dropped, not refused.**
  Replying at all — even `REFUSED` — still sends a packet to a possibly-forged source, which is
  the amplification behaviour itself. Silence is the correct answer.
- **Per-source rate limit**, default 50/s, with a bounded map so a spoofed-source flood cannot
  grow it without limit.
- **A loud startup warning** when bound to `0.0.0.0` *and* forwarding.
- Phase 4 firewall rules stay scoped to `192.168.137.0/24`.

Verified: a query from `10.0.0.213` (this machine's own LAN address, outside the hotspot range)
was dropped with no reply; loopback was answered. With `--rate 10`, 30 queries produced exactly
10 answers.

> **One constraint that prevents the tidiest fix, and it must carry into production.**
> Binding only to `192.168.137.1` is not available to the game at startup: **that address does
> not exist until the hotspot is running**, and `:53` has to be held *before* tethering starts
> to win the race against ICS. The spike only proved ICS will not *evict* a held port — whether
> it claims a free one is untested, and assuming otherwise would be a guess.
>
> So the shipped code will very likely also bind `0.0.0.0`, which makes **the source guard the
> load-bearing protection rather than a backstop**. It must not be treated as optional, and the
> narrow bind should be used wherever the timing does allow it.

## 5. ✅ FIXED — HTML injection through player names

*Fixed 2026-08-20 in [controller/js/app.js](controller/js/app.js). The scoreboard now builds
its two `<span>` elements and assigns `.textContent`, so a name can never be markup. Kept below
because the same mistake is easy to reintroduce anywhere a name is rendered.*

[controller/js/app.js:252](controller/js/app.js:252) builds the scoreboard with `innerHTML`:

```js
row.innerHTML = `<span>${player}</span><span>${score}</span>`;
```

`player` is server-supplied, and `sanitizePlayerId` ([relay/server.js:94](relay/server.js:94))
only does `trim().toUpperCase().slice(0, 16)`. **Uppercasing is not sanitisation — HTML tags are
case-insensitive.** So a name of `<H1>OWNED</H1>` renders as markup in every other player's
browser.

Full script execution is *constrained* by the 16-character cap rather than prevented — `<SVG/
ONLOAD=A()>` is exactly 16. The cap is the only thing standing in the way, and caps get raised
later by people who do not know they are load-bearing.

**What was applied**

- `textContent` instead of `innerHTML`, and `replaceChildren()` instead of `innerHTML = ''`.
  A comment at the call site records *why*, since the next person to build a row there will
  reach for a template literal exactly as the original author did.
- Verified no other `innerHTML` / `insertAdjacentHTML` / `document.write` remains anywhere in
  `controller/`, and that every other place a name is rendered already uses `textContent`.

**Still outstanding, deliberately not changed**

- Server-side name restriction to something like `[A-Z0-9 ]` would be reasonable defence in
  depth — but only *as well as* the above, never instead of it. Not applied, because escaping at
  the render site is the fix that cannot be bypassed and adding input filtering can create false
  confidence.
- [ResultsScreen.cs:137](Assets/Scripts/UI/ResultsScreen.cs:137) renders `PlayerId` through
  `GUILayout.Label`. Worst case is rich-text tag mischief (`<color=…>`) on the TV, which is
  cosmetic rather than dangerous — **unverified either way**, and worth a look when that screen
  is next touched.

## 6. 🟡 Relay exposure while the game is running

The relay is well hardened already, and this section is about what remains rather than a
catalogue of failures. Verified present: `crypto.randomInt` room codes over an unambiguous
24-character alphabet, a 4096-byte message cap, 60-messages-per-second rolling rate limit,
8-player room cap, `JSON.parse` in try/catch, and a path-traversal guard on static files.

What is left:

- **No per-IP connection limit.** The rate limit is per *socket*, so opening many sockets
  sidesteps it. *Mitigation:* cap concurrent connections per source address.
- **Room codes are brute-forceable in principle** — 24⁴ ≈ 332k. The prize is joining a party
  game, so impact is low. *Mitigation:* cap failed `JOIN_ROOM` attempts per socket.
- **The path-traversal guard uses a string prefix test** —
  `resolved.startsWith(STATIC_DIR)` also accepts a sibling directory named
  `controller-something`. Not currently exploitable, since nothing else sits there.
  *Mitigation:* compare with `path.relative(STATIC_DIR, resolved)` and reject results that are
  absolute or begin with `..`.

## 7. 🟡 Distribution and the things that outlive the game

- **Bundled Node ages.** A pinned `node.exe` stops receiving security fixes the day it ships.
  *Mitigation:* pin a version, watch advisories, and have an update path. The attack surface is
  small — but "small" is doing real work there, so keep it small.
- **Firewall rules persist.** They survive the game not running. *Mitigation:* already scoped to
  the hotspot subnet and specific ports; scope by program path too, and **remove on uninstall**
  (already in Phase 4 — this is why).
- **The installer asks for administrator rights.** That is a genuine trust request from a
  player. *Mitigation:* **code-sign the installer and binaries.** An unsigned executable that
  requests elevation, opens firewall holes and spawns a listening child process is
  indistinguishable from malware — to antivirus heuristics and to a careful user alike.
- **The hotspot passphrase appears on a television.** Fine in a living room; a leak on a stream
  or a video call. *Mitigation:* generate it cryptographically at random **per session**, never
  derive it from the room code, and tear the hotspot down when the match ends.

---

## Priority

| | Item | Status |
|---|---|---|
| 1 | **DNS source guard + rate limit** (§4) | ✅ **Done** — the only issue that could harm people not playing the game |
| 2 | **`textContent` in the scoreboard** (§5) | ✅ **Done** |
| 3 | **Code-sign the installer** (§7) | Open — gates whether anyone can safely trust the admin prompt |
| 4 | Per-IP connection cap, `path.relative` guard (§6) | Open — hardening on an already-solid relay |
| 5 | Hotspot passphrase hygiene, teardown on exit (§7, §3) | Open — cheap, prevents a class of embarrassment |
| 6 | Confirm rich-text handling on the results screen (§5) | Open — cosmetic at worst, currently unverified |

**Carry §4's reasoning into the shipped code.** The fixes above are in the *spike*, and the
production relay has not been written yet. The source guard in particular is not a detail to
reimplement from memory — it is load-bearing precisely because the narrow bind is unavailable.

§1–§3 are not on this list because they are **properties to preserve, not defects to fix**. The
work there is remembering them when the next feature is designed.

§1–§3 are not on this list because they are **properties to preserve, not defects to fix**. The
work there is remembering them when the next feature is designed.
