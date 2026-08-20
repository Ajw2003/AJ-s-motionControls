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

## 2. Protecting guests *from the host*

The sharpest question anyone has asked about this design: when five friends join your hotspot,
what have you just been handed?

### First, precisely what the host can and cannot see

Two separate things get conflated here, and the difference decides how much work this deserves.

| | Host can see? |
|---|---|
| **Which sites a phone talks to** — destination IPs, TLS SNI, packet sizes and timing | **Yes.** Unavoidable for any access point. |
| **DNS lookups**, if the phone uses the host as its resolver | **Yes** — a readable list of every domain requested. |
| **Contents of HTTPS traffic** — messages, passwords, page content | **No.** Decrypting it needs a root CA installed on the *phone*, which the host cannot do. |
| **Contents of plain HTTP traffic** | Yes, but almost nothing is plain HTTP now. |

So the accurate statement is **metadata and DNS, not content**. A host learns *that* a guest
opened a bank's app; they cannot read anything inside it. That is materially smaller than "all
their traffic can be read", and it is exactly the exposure of every coffee shop, hotel and
friend's-house Wi-Fi anyone already uses.

> **A conflation worth clearing up:** the "open resolver" problem in §4 was **not** about reading
> guests' traffic. It was that *strangers outside the party* could abuse the resolver as a DDoS
> amplifier. Those are unrelated problems with unrelated fixes, and §4 is already fixed.

Two further points in guests' favour: modern phones increasingly defend themselves — Android
**Private DNS** (DoT) and iOS **iCloud Private Relay** both take DNS out of the host's view
entirely — and encrypted SNI/ECH is eroding the hostname leak too.

### The reframe that does most of the work

**The hotspot is the fallback, not the default.** On an ordinary network — which the 5-player
session proved works well, across two routers — **the host is not in the path at all**. The
router is, exactly as it always was. Guests' traffic never touches the host machine.

The host only becomes the network when guests *cannot otherwise join*, and there the honest
alternative is not "a more private option", it is **no game**. That makes it a visible, consented
trade rather than a hidden one — provided the game actually says so, which is why the on-screen
notice below is a requirement and not a nicety.

### ✅ CONFIRMED — the hotspot can carry the game without carrying the internet

**Spiked and verified on a real phone, 2026-08-20.** This is the mitigation that makes the
hotspot feature defensible, and it works.

The game needs a phone to reach *this PC*; it never needed to give that phone internet. Measured
behaviour, contradicting the guess in an earlier draft of this document:

```
BEFORE tethering:  Ethernet 4                 forwarding Disabled
DURING tethering:  Ethernet 4                 forwarding Enabled   <- ICS turns this on
                   Local Area Connection* 12  forwarding Enabled
Get-NetNat entries: 0
```

ICS routes with **plain IP forwarding**, not an opaque NAT engine — so
`Set-NetIPInterface -Forwarding Disabled` *is* the lever after all, and ICS does not revert it.
With forwarding off, a phone on the hotspot loaded `http://play.local-ip.sh/` normally and
**could not load any ordinary website.**

**What this buys, and it is more than "we promise not to look":**

- Guests' real browsing **never arrives at the host**, so there is nothing to observe, log, or
  subpoena. Not a policy — an absence of data.
- **Android keeps mobile data as its default route** for a Wi-Fi it has judged to have no
  internet, using the Wi-Fi only for the local subnet. So at a normal party, with mobile data
  on, guests' traffic *and their DNS* go over cellular and never touch the PC.
- §3's liability problem disappears with it: nothing exits via the host's IP address, so nothing
  is attributed to them.

### What this does *not* eliminate — read before claiming it is solved

The forwarding fix removes **liability** essentially completely, and removes **observability**
substantially but not entirely. Those are different claims and the difference matters.

**Gone, and genuinely gone:**

- Guests' traffic contents, destinations, SNI, sizes and timing — none of it reaches the host
- Attribution: nothing exits via the host's IP address, so nothing is traceable to them
- The acceptable-use breach on school and corporate networks

**Still visible to the host:**

- **DNS queries.** A phone asks `192.168.137.1:53`, which is **host-bound traffic, not
  forwarded** — the forwarding flag has no effect on it. The host can see any domain a guest
  looks up.
  - *Reduced in practice* by Android moving to cellular for a Wi-Fi with no internet, so most
    phones stop asking us almost immediately.
  - **Not reduced at all for the most captive guest**: a tablet with no cellular, a phone with
    mobile data off, or anyone with no signal keeps using our Wi-Fi and our resolver. Their
    connections fail, but the *lookups still arrive first*. That guest's browsing intentions are
    visible even though their traffic is not.
- **Local-subnet chatter.** Being on the same LAN exposes DHCP hostnames (often a real name —
  "Sam's iPhone"), mDNS/SSDP announcements, and MAC addresses. Modern phones randomise MACs per
  network, which helps; the hostname usually still leaks.
- **The game's own traffic**, which is the consented part.

**And the structural caveat:** all of this holds only while the host runs *unmodified* software.
A host who re-enables forwarding sees everything again. No in-product control can prevent that —
it is the same trust model as any Wi-Fi you join, which is why §7's code-signing is the only
thing that speaks to it at all.

**So the accurate claim is:** *"the hotspot does not carry your internet traffic, and nothing you
do is attributable to the host"* — **not** *"the host can see nothing."* The first is defensible
and worth saying on screen. The second would be false.

The remaining mitigation is the one already on the plan: answer only the game's own name,
`NXDOMAIN` everything else, and never log queries. That is policy rather than architecture, and
should be described as such.

> **Follow-on problem, and it is a real one.** `Set-NetIPInterface -Forwarding` needs
> administrator rights, and **ICS re-enables forwarding every time tethering starts** — so this
> is a runtime action, once per session, not a one-off at install. A UAC prompt per party would
> defeat the zero-setup goal outright.
>
> The way out is a **scheduled task registered at install time with highest privileges**, which
> the game triggers without a prompt. It must take **no parameters** and do exactly one
> hardcoded thing — disable forwarding on the `192.168.137.x` interface. A task that accepted an
> interface name from a caller would be a local privilege-escalation vector, which would be an
> unusually silly way to undo a privacy win.

### What is not achievable, stated plainly

- **Whoever runs the access point sees metadata.** No configuration removes this while the PC is
  the AP.
- **A malicious host can modify their own build.** No control in the software protects against
  the person running it. This is the same trust model as joining anyone's Wi-Fi, and it is the
  reason §7's code-signing matters — a signed build is at least evidence of *which* software is
  running.
- Building a bespoke "network app" does not escape any of this: browsers need IP over Wi-Fi, and
  Windows offers no user-mode AP that is not ICS. See §2.1.

**Mitigations to apply regardless**

- **Answer only the game's own name; NXDOMAIN everything else; never log queries.** Already the
  behaviour when `--upstream` is unset — make that the shipped default rather than an option.
- **Do not run the resolver at all in LAN mode.** Phones use the router's DNS there, so the game
  has no business being in that path.
- **Tear the hotspot down when the match ends.** An access point left running after the party is
  both a privacy and a security liability.
- **Say it on screen**, once, in plain words: *"Guests' phones will connect through this PC. It
  carries the game only and does not share your internet."* The second half of that sentence must
  become true first.

## 2.1 Could a purpose-built app replace the Windows hotspot?

Asked directly, and worth answering directly: **no — and the reason is useful, because it tells
you where the effort actually pays.**

| Alternative | Why it does not work |
|---|---|
| **Bluetooth / Web Bluetooth** | Browsers cannot act as a BT network client. Web Bluetooth is BLE, peripheral-oriented, wrong direction, and far too slow for 20 Hz aim data. |
| **Wi-Fi Direct / Wi-Fi Aware** | No browser API. A phone browser cannot address a peer over either. |
| **A custom software access point** | Windows exposes no user-mode AP other than Mobile Hotspot. Anything else means shipping a network driver — a signed kernel-mode component, which is a different project with a much worse security story than the one being avoided. |
| **A relay app installed on each phone** | Defeats the entire premise. The game's core promise is *scan a QR, no app download*. |

The controller is a web page, so it needs IP over Wi-Fi, and on Windows that means Mobile
Hotspot, and Mobile Hotspot means ICS. There is no way around the funnel.

**But the funnel's width is ours to set.** The right conclusion is not "build a different
network", it is **"constrain the network we are given"** — which is exactly the no-internet-
sharing spike in §2. That is a far smaller piece of work than a bespoke stack and it is the only
one that changes what the host can observe.

Worth noting a separate helper process is needed anyway: Unity cannot call WinRT directly
(ZeroSetupPlan Phase 3.5), so something outside Unity must drive tethering. **That helper is the
natural home for this**: start the hotspot, apply the forwarding restrictions, and guarantee
teardown on exit. So the instinct behind the question was right — it is the scope that shrinks,
from "a network application" to "the process already on the plan, doing one more job."

## 3. ✅ LARGELY RESOLVED — the host's liability for shared traffic

Windows tethering shares the uplink by default, so everything a guest does would exit via **the
host's IP address** and be attributed to them. On a school or corporate network that also
commonly breaches acceptable-use policy — awkward, given the hotspot's best use case is exactly
those networks.

**§2's forwarding fix removes this.** With no internet crossing the hotspot, nothing exits via
the host's address and there is nothing to attribute. The acceptable-use problem goes with it:
the PC is not redistributing the network, it is running a LAN for a game.

**Still worth doing**

- Say so on screen, once, where the host turns the hotspot on. Not a EULA — one line, and it can
  now honestly say the hotspot does *not* share the connection.
- Stop tethering when the game closes. An access point left running after the party is a
  liability whether or not it routes anything.

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
| 3 | **Hotspot carries the game, not the internet** (§2) | ✅ **Spiked and confirmed** — implementation still to do, via the scheduled task described in §2 |
| 4 | **Code-sign the installer** (§7) | Open — gates whether anyone can safely trust the admin prompt |
| 5 | Per-IP connection cap, `path.relative` guard (§6) | Open — hardening on an already-solid relay |
| 6 | Hotspot passphrase hygiene, teardown on exit (§7, §3) | Open — cheap, prevents a class of embarrassment |
| 7 | Confirm rich-text handling on the results screen (§5) | Open — cosmetic at worst, currently unverified |

**Carry §4's reasoning into the shipped code.** The fixes above are in the *spike*, and the
production relay has not been written yet. The source guard in particular is not a detail to
reimplement from memory — it is load-bearing precisely because the narrow bind is unavailable.

§1–§3 are not on this list because they are **properties to preserve, not defects to fix**. The
work there is remembering them when the next feature is designed.

§1–§3 are not on this list because they are **properties to preserve, not defects to fix**. The
work there is remembering them when the next feature is designed.
