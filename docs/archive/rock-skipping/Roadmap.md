# Roadmap — what 0% to 100% means

**Tier 2.** This document defines *done*. It does not say where the project is — that is
[`ProjectState.md`](ProjectState.md), and the two are deliberately separate so that progress can
move without the definition of the target moving with it.

Ten milestones, one per 10%. Each is written so that **someone else could check it**: the
acceptance criterion is an observable outcome, not a feeling that the work is finished.

> **Granularity is a choice, and this is the choice.** 10% bands put each milestone at roughly
> one to two weeks of real work — coarse enough that a milestone is a meaningful unit, fine
> enough that "where am I" has an answer better than "somewhere in the middle". Finer bands were
> considered and rejected: at 1% this becomes a backlog that has to be re-cut every time
> priorities move, and it would need re-cutting often, because this project reverses hosting and
> physics decisions regularly.

---

## What 100% is

**A person who has never seen this repository downloads one file, runs it, and hosts a party for
their friends in their living room — with no internet, no router, no configuration, and no
instructions from you.** Their guests scan a QR code and play. Nobody types an address, installs
an app, or troubleshoots a certificate.

Everything below is on the way to exactly that sentence. A milestone that does not move toward
it does not belong on this list.

**What 100% is not.** Not a commercial release: no store pages, no localisation, no
achievements, no online/remote play, no platform beyond Windows. Those are a different roadmap.
This one ends at "a stranger can play it".

---

## The milestones

| # | % | Milestone | One line |
|---|---|---|---|
| 1 | 10 | Infrastructure spike | The scaffolding exists and survives scene loads |
| 2 | 20 | Motion pipeline | A phone's rotation reaches Unity intact |
| 3 | 30 | Skip physics | A stone bounces, and the throw is what decides how well |
| 4 | 40 | Turns, scoring, multiplayer | Eight people can play a match to a result |
| 5 | 50 | Local hosting and secure context | The PC serves the controller page over HTTPS itself |
| 6 | 60 | Offline hosting | A match runs with no internet and no router |
| 7 | 70 | Frictionless join | Joining is a scan, and a returning phone is not asked twice |
| 8 | 80 | Rock variety and selection | Six stones that play differently, and a way to pick one |
| 9 | 90 | Presentation | It looks and sounds like a finished game |
| 10 | 100 | Zero-setup distribution | A stranger installs and plays |

---

## 1 — Infrastructure spike (10%)

The load-bearing scaffolding, before any of it is worth building on.

**Contains:** the event system ported and made multi-scene safe; the four-scene structure
(Bootstrap → Lobby → Game → Results) with persistent managers; the Node relay written and
hardened with its own test suite.

**Acceptance:** `RockSkipping/Verify/Run Event System Verification` proves publish/subscribe
survives a scene load and that destroyed subscribers are dropped; `cd relay && npm test` passes.

**Why first:** every later milestone assumes an event can cross a scene boundary and a phone can
reach the PC. Both were ported from another project and neither worked unmodified.

---

## 2 — Motion pipeline (20%)

The phone's orientation arrives in Unity as the same rotation the player is holding.

**Contains:** sensor normalization on the phone (iOS and Android differences resolved *before*
sending, so Unity never sees a platform); permission gating for `DeviceOrientationEvent`; the
grip-relative quaternion delta; one conversion into Unity space; the aim preview drawing a
predicted arc.

**Acceptance:** tilt the phone ten degrees, the on-screen stone tilts ten degrees, on both iOS
and Android, in the pose the game is actually played in — phone upright, screen facing the
player, aimed at a television.

**Why it is its own milestone:** that pose sits exactly on a gimbal singularity, and the first
two implementations broke there. See [`4-systems/MotionPipeline.md`](../../4-systems/MotionPipeline.md).

---

## 3 — Skip physics (30%)

A stone thrown at water skips, sinks, or fails, and *the throw* is what decides which.

**Contains:** the custom bounce solver; the energy model derived from bounce geometry rather than
an authored retention constant; spin as the stabiliser that holds attack angle through impact;
the three forgiveness bands; the aim preview running the same solver speculatively.

**Acceptance:** `RockSkipping/Verify/Print Skip Curve` produces a speed/angle/roll sweep in which
a better throw beats a worse throw, and every run satisfies the energy invariant. Two different
throws produce visibly different results without any tuning edit between them.

**Why it is not "physics works":** an earlier model had 25 tuning fields fighting each other, and
a one-point tuning edit moved the result more than the player's own throw did. The milestone is
the throw mattering, not the simulation running.

---

## 4 — Turns, scoring, multiplayer (40%)

A real match, with real people, to a real result.

**Contains:** the player roster; match phases (Lobby / Choosing / Playing / Results); turn
rotation over a configurable number of rounds; push-based scoring; late join; player names and
colours; the results screen.

**Acceptance:** a cross-platform session with five or more phones (iOS and Android together)
completes a full match — every player throws every round, scores are correct, someone wins, and a
player who joins mid-lobby or drops and returns is handled without restarting.

---

## 5 — Local hosting and secure context (50%)

The PC hosts the game itself. No cloud service is involved in any way.

**Contains:** the relay running as a child process of the game; HTTPS with a real certificate,
obtained and renewed with no human involved; the controller page served from the host; the
hosted-relay design removed rather than left as a fallback.

**Acceptance:** press Play in Unity; a phone on the same network opens the join URL, gets a valid
certificate, and its motion sensors unlock. Nothing was configured first and no cloud account
exists.

**Why this is half the project:** phone motion sensors are gated on a secure context. Without
this milestone there is no game, only a demo that works on the developer's machine.

---

## 6 — Offline hosting (60%)

The living room has no internet. The game does not care.

**Contains:** the PC raising its own Wi-Fi network via Windows Mobile Hotspot; DHCP for guests;
the relay answering DNS for its own hostname on the hotspot subnet; the host resolving its own
relay via a hosts entry; guest isolation verified rather than assumed; the certificate cached so
no network is needed at play time; one-time elevated machine setup automated behind a single UAC
prompt.

**Acceptance:** unplug everything. Start the game. A phone that has never seen this network joins
by QR and plays a full match.

**Why it is separate from milestone 5:** milestone 5 needs a working network. This one needs the
PC to *be* the network, which is a different problem with different failure modes — three of
which were genuine blockers that each looked like a Windows limitation and were not.

---

## 7 — Frictionless join (70%)

Joining stops being a ritual.

**Contains:** QR codes for the Wi-Fi payload and the game URL (never asking anyone to type or
hunt for an address); a returning phone rejoining itself without repeating the sequence; the
hotspot SSID, passphrase and room code surviving a restart; the device funnel telling the TV how
many phones are stuck at each stage, so the host can help the person who is stuck.

**Acceptance:** a guest who played last week arrives, scans once, and is in. A guest who is stuck
is visible on the TV as stuck, at the stage they are stuck at.

---

## 8 — Rock variety and selection (80%)

Six stones that feel different, and a way to choose one that is worth doing.

**Contains:** the rock catalog and per-rock definitions built from the mesh source; the strict
split between what a rock owns (three bounded multipliers) and what is global; the selection
state and its wire format; pointing the phone at the shore to highlight a stone and confirming
it; a host settings panel; telemetry and an offline sweep to compare rocks against each other.

**Acceptance:** six rocks are selectable, each is **visually distinct on the TV and named on the
scoreboard**, a player picks one by pointing their phone at the shore, and the pick survives
into their throw.

**This acceptance criterion changed on 2026-09-08**, and the change is the point. It previously
read "each plays measurably differently under `Print Skip Curve`". That is now unmeetable by
design: [`plans/presentation-and-ui.md`](../../plans/presentation-and-ui.md) D5 settles that the rocks
stay **mechanically identical** and differentiate visually only — distinct meshes and
silhouettes, named on the scoreboard, with the player's colour always winning the tint, because
identity in a five-phone party game outranks a cosmetic choice.

**Mechanical differentiation is deliberately deferred, not abandoned.** Three multipliers across
six rocks is a balance problem with no playtest budget behind it. `SkipTuning.cs:22` records why
the physics is being judged bare first, and that reasoning still stands. When it is picked up,
do it along **one** legible axis — flat-and-light skips more but travels less, heavy travels far
but skips less — because one axis is explainable in a sentence on the phone's choose screen and
three axes are noise.

---

## 9 — Presentation (90%)

It stops looking like a prototype.

**Contains:** audio — the skip pitch ladder, sink, fail, score and win sounds, and the scheduler
that stops same-frame sounds colliding; trails and water response; camera framing; music. Plus,
as of 2026-09-08, the whole of [`plans/presentation-and-ui.md`](../../plans/presentation-and-ui.md):
the stylised-flat art direction and environment presets, the shore, the retirement of IMGUI in
favour of UI Toolkit, and **the in-play HUD** — which does not currently exist in any form.

**Acceptance:** someone watching a match from the couch, who has not been told what to look for,
does not notice anything missing. Concretely, and this is new: they can see **whose turn it is
and how many skips the throw is on, without asking anyone.**

**Two known blockers, and neither is code:**

- **Where source clips come from** — licensed, commissioned, or generated. Still unanswered. The
  systems work without clips; the milestone does not close without them.
- **Which typeface**, and under what licence. The project contains **no font asset at all** and
  renders every screen in Unity's built-in Arial. Same class of blocker as the clips, found
  2026-09-08. D10 of the plan settles the shape of the answer — a heavy-weight SIL-OFL face
  committed to `Assets/Fonts/`, never fetched at runtime — but not the specific face.

---

## 10 — Zero-setup distribution (100%)

The sentence at the top of this document.

**Contains:** bundling Node and the relay's dependencies inside the build; getting `relay/` and
`controller/` into the build at all (they live outside `Assets/`, so Unity never copies them);
the game's own defaults being correct with no override files; the installer, compiled and
signed; a seed certificate shipped so a first launch works before it can reach anything.

**Acceptance:** a person who has never seen the repository is handed one file. They install it,
run it, and host a party. You are not in the room and they do not phone you.

**The four blockers are known and unchanged** — see [`ZeroSetupPlan.md`](../../ZeroSetupPlan.md). The
third one, that `relay/` and `controller/` are not in the build, fails before any of the others
are reached.

---

## Keeping this honest

- A milestone is done when its **acceptance criterion** has actually been checked, not when the
  code exists. "Written but never run" is not done, and saying so is the whole value of writing
  the criteria down.
- When a milestone's status changes, update [`ProjectState.md`](ProjectState.md) — not this file.
  This file changes only when the *definition* changes.
- If work is happening that fits no milestone here, that is worth noticing. Either it belongs to
  a milestone and this file is missing it, or it is not on the way to 100% and should be
  questioned.
