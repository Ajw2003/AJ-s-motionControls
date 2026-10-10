# Verification

**Source:** `Assets/Scripts/Verification/`, `Assets/Editor/SceneCaptureRunner.cs`, `Assets/Tests/EditMode/`

## What it owns

The in-editor and headless checks that run the real code instead of a copy: the event-system and rotation checks, the skip curve, the reality check against published numbers, the scene capture, throw telemetry, and the EditMode tests.

## How it works

The menu items that run these checks are listed in `Assets/CLAUDE.md` under Running and testing; the notes in the sections below are the author's reasoning moved verbatim from the source comments.

### `Assets/Scripts/Verification/RockTelemetry.cs`

#### One CSV row per real throw, appended and closed each row so a crash keeps the corpus
<!-- ref:59aa -->

Was `Assets/Scripts/Verification/RockTelemetry.cs:11-38`.

Records one CSV row per real throw — the corpus of what players actually do with
the throw-driven model, next to what SkipCurve measures offline.

This used to record the Authored/Derived comparison (docs/plans/rock-choosing-system.md
Part 6); that apparatus is gone (docs/plans/Plan-ThrowDrivenSkipModel.md), and so is
the `tuningSet` column. What is left is the honest thing to keep measuring: the raw
signal a real swing produced (swingSpeed, spinRate) against the three axes the bounce
now judges (pitchDeg, rollDeg, yawDeg) and what it scored. yawDeg is always 0 at
launch — the velocity starts aligned with the stone's heading by construction — so
this records the number that would actually separate a straight throw from one that
curved, once something downstream tracks sideslip at landing rather than at release.

Written beside skip-curve.txt (repo root in the Editor, next to the executable in a
build), the same file-beside-the-exe convention as relay-url.txt and
match-settings.json. Append-only and closed after every row: a session that crashes,
or a host that alt-F4s out of a match, keeps every throw up to that point. Losing a
corpus to a buffered write would cost a play session that cannot be re-run, because
the people are what made it.

Reads rather than publishes — it is an observer, and nothing in the game may depend
on it running. Turning it off in Bootstrap changes no behaviour, only what is recorded.

The 73 rows logged under the old model (commit 34932cc) are not comparable to
anything this writes — see the "archive, don't delete" note on rock-throws.csv in
the plan's Risks section. That file was archived, not appended to, when this changed.

### `Assets/Scripts/Verification/SkipCurve.cs`

#### Sweeps swing speed and all three phone axes through the real solver
<!-- ref:15bf -->

Was `Assets/Scripts/Verification/SkipCurve.cs:9-35`.

Prints what the current tuning actually does, by running the real solver.

Rewritten for the throw-driven skip model
(docs/plans/Plan-ThrowDrivenSkipModel.md) — there is one tuning per rock now, not
two sets to compare, so this sweeps raw swing speed and all three phone axes
(pitch, roll, yaw via roll-induced sideslip) through SkipPhysics
directly against the catalog's own assets. There is nothing to keep in sync, and
no way for the measured curve to disagree with the game.

**The acceptance criterion for this whole change:** throw speed must move skip
count more than any per-rock multiplier does. Concretely, doubling swing speed
should beat driving a multiplier to its bound — see the "speed vs multiplier"
check in Verdict, which is the number this report exists to answer.

Bounces are counted from SkipPhysics.Step's own `Bounced` flag
rather than reconstructed by comparing consecutive path heights against WaterY.
The old position-comparison approach had a contact-detector off-by-one
(docs/Reference-SkipTuning.md) that a face angle of exactly 0 could trigger — and
a face angle of 0 is now the ordinary case, since pitch comes straight off the
phone's tilt rather than being biased toward an authored optimum. Reading the
solver's own signal removes the epsilon comparison entirely.

With no catalog (or an empty one) this falls back to a single-rock report against
Resources/SkipTuning, which is what the game itself falls back to as well.

#### The attitude is built directly in Unity space
<!-- ref:c7c2 -->

Was `Assets/Scripts/Verification/SkipCurve.cs:303-313`.

Run one throw through the real solver and measure the shape of the result.

Builds the attitude directly in Unity space — SkipPhysics.CreateLaunch takes
attitude already converted, and PhoneAttitude.ToUnity's device-frame round
trip has nothing to add here. Pitch comes from the forward vector's own
elevation; roll is then applied as a rotation about that same forward axis, so
it changes bank without touching pitch — the same "one rotated vector" property
SkipPhysics.Step relies on.

### `Assets/Scripts/Verification/SkipReality.cs`

#### Drives DiscContact and RigidDisc against ReferenceStone independent of SkipConstants
<!-- ref:72e0 -->

Was `Assets/Scripts/Verification/SkipReality.cs:9-30`.

The R1-R10 reality check: drives DiscContact/RigidDisc
directly against ReferenceStone and compares the result to the
published numbers in docs/Findings-RealSkipPhysics.md §3. Mirrors
SkipCurve's Run()/Build()/ResultPath structure, writing
skip-reality.txt at the repo root.

This harness is deliberately independent of `SkipConstants`/`SkipTuning`/
`SkipPhysics` — docs/plans/real-skip-physics.md is explicit that Phase 1's
harness "drives DiscContact/RigidDisc directly, not through SkipPhysics", precisely
so this file can exist without the gameplay solver changing underneath it. Every
constant a real throw would read off SkipConstants/SkipTuning is instead read off
ReferenceStone or defined locally below, with a citation.

**Tolerances are loose on purpose.** docs/Findings-RealSkipPhysics.md §5 item 3:
"[B] assumes tilt θ is constant through a collision and derives stability
separately. We integrate attitude live instead, so R1-R7 test a STRONGER model than
the one that produced these closed forms — expect agreement in order of magnitude
and in trend, not to three digits." Every check below states its tolerance and why,
rather than asserting the closed-form number to full precision.

### `Assets/Tests/EditMode/DiscContactTests.cs`

#### Closed-form geometry tests
<!-- ref:e6d5 -->

Was `Assets/Tests/EditMode/DiscContactTests.cs:7-21`.

Closed-form geometry coverage for Assets/Scripts/Game/Physics/DiscContact.cs —
wetted area and its centroid against analytic values at known depths, plus the
symmetry the geometry must obey. This file is the reason DiscContact was written as
pure, static, closed-form maths in the first place: everything here is checkable
against a formula with no simulation involved.

Centroid derivation (not separately published in [B] — DiscContact.cs cites this):
integrating the same circular-segment area DiscContact.WettedArea is built from
(docs/Findings-RealSkipPhysics.md §2 / [B] Eq. 9) against x dA gives a first moment
of (2/3)*(R^2-d^2)^1.5, so centroid distance x̄ = moment / area. At d = -R (fully
wet) that reduces to 0 (a whole circle's centroid is its own centre); at d = 0 (half
wet) it reduces to the textbook semicircle centroid 4R/(3*pi).

### `relay/fake-host.js`

#### What the fake host does
<!-- ref:93e6 -->

Was `relay/fake-host.js:3-15`.

A stand-in TV host for exercising a phone against a relay without Unity.

It does what TurnManager does for one player at a time. Every phone that joins gets
TURN yourTurn:true. Each THROW is logged, and SETTLE_MS later (the stone "settling")
TURN is sent again. Once a second it prints how many AIM frames each phone sent, which
is how to see the stream stop during a stone's flight, or a stall's backlog land at once.

  node relay/fake-host.js                      (relay on ws://127.0.0.1:3000)
  RELAY_URL=ws://127.0.0.1:3998 SETTLE_MS=5000 node relay/fake-host.js

Stop it with Ctrl+C.

### `relay/test-dns.js`

#### What the DNS tests cover and why
<!-- ref:4117 -->

Was `relay/test-dns.js:3-14`.

Tests for the local DNS responder. Run with: npm test

Two halves, deliberately. The pure wire encoding and the guards are exercised directly,
because that is where the subtle mistakes live and none of it needs a socket. Then a real
responder is bound on a high port to prove the pieces work together.

The AAAA and source-guard cases are not padding. Both were wrong in the first draft, and
both fail in ways that look like something else entirely: NXDOMAIN on AAAA makes a phone
abandon a name it could have resolved over IPv4, and a missing source guard turns this
into a DDoS amplifier without changing anything a local test would notice.

### `relay/test-funnel.js`

#### What the funnel tests cover
<!-- ref:044b -->

Was `relay/test-funnel.js:3-13`.

Tests for the per-device join funnel. Run with: npm test

The funnel decides what the TV says while people are joining, off signals that arrive out
of order, from three different layers, keyed only by IP. Every case below is one of the
ways that combination can lie:

  - a stage regressing would make the TV nag someone mid-throw,
  - an untracked address being counted would invent a phone on a developer's machine,
  - a device never expiring would leave the TV nagging about a guest who went home.

### `tools/physics/HeadlessSolver/Program.cs`

#### What the HeadlessSolver drives
<!-- ref:9d9d -->

Was `tools/physics/HeadlessSolver/Program.cs:7-17`.

<summary>
Drives the project's real <see cref="Game.DiscContact"/> / <see cref="Game.RigidDisc"/>
outside Unity, so the skip model can be calibrated in seconds instead of one Editor
menu click at a time.
</summary>
<remarks>
The run loop here is the one that becomes SkipPhysics.Step in phase 2 — flight under
gravity at a coarse step, contact sub-stepped finely, attitude integrated throughout.
Calibrating it here and then porting the settled numbers is the whole point: the
alternative is guessing in the Editor.
</remarks>

## Invariants

### `tools/audit/check-standards.js`

#### The coding-standards audit
<!-- ref:1334 -->

Was `tools/audit/check-standards.js:4-19`.

Coding-standards audit for RockSkipping.

Plain Node, zero dependencies. Walks the repo, runs a flat list of named
checks, prints one PASS/FAIL line per check with a count, and exits
non-zero if any check fails.

This file is meant to be read and edited by a human without an agent
present -- keep each check a small, self-contained function that returns
{ pass, count, detail } and is registered in the CHECKS list at the
bottom.

Comment DENSITY is reported as an informational number only and must
NEVER cause a failing check -- high density is this codebase's deliberate
strength.

## Traps

- **Running the phone checks on Windows (2026-10-10).** Playwright is pinned in `tools/package.json`
  (`npm ci --prefix tools`, then `npx --prefix tools playwright install chromium`). Chromium 156
  answers the page's motion-permission request "denied" unless the context grants
  `accelerometer` and `gyroscope`; `tools/verify-gamepad.js` does. It also presses the Wii
  layout's Centre (the PC must see exactly bit 17) and Lock rotation twice (lock, then unlock).
  Emulated mobile Chrome really locks; Safari on iPhone cannot be emulated here.

### `Assets/Editor/SceneCaptureRunner.cs` — Same shape as RunVerification but leaves Bootstrap enabled and must run without -nographics
<!-- ref:94d2 -->

Was `Assets/Editor/SceneCaptureRunner.cs:9-28`.

Batchmode entry point for SceneCapture — plays the game and writes a
PNG of the TV screen, so a change to the look can be checked without a human.

Deliberately the same shape as `ProjectSetup.RunVerification`, which is the
pattern in this repo that is known to work headlessly: open Bootstrap, add a driver
component, enter play mode, and let the CALLER poll for the output file. Exiting the
editor from the domain reload that follows play mode is unreliable in batchmode, so
the runner script kills the process once the marker appears.

**Run without `-nographics`.** The event verification uses that flag because
it renders nothing; here it produces a blank image, because there is no graphics
device to read pixels back from.

Unlike RunVerification this leaves Bootstrap ENABLED. The whole point is to
photograph the game as a player sees it, which means the real boot path, the real
Lobby load and the real managers — a disabled Bootstrap would capture an empty scene
and prove nothing about the thing being changed.

### `Assets/Scripts/Verification/SceneCapture.cs` — Capture must run without -nographics or the image is blank
<!-- ref:ca6e -->

Was `Assets/Scripts/Verification/SceneCapture.cs:7-24`.

Plays the game headlessly and writes a PNG of what the TV would show.

Exists so a change to the LOOK of the game can be checked without a human pressing
Play and describing the result. Four rounds of art changes were shipped blind and
every one was wrong in a new way; a screenshot the author can actually look at is
the difference between tuning and guessing.

Same shape as EventSystemVerification: driven headlessly by an editor
entry point (`SceneCaptureRunner.Capture`), writes a file at the project root,
and the caller polls for that file rather than waiting on the process — exiting the
editor from the domain reload after play mode is unreliable in batchmode.

**Must run WITHOUT `-nographics`.** That flag is right for the event
verification, which touches no renderer, and fatal here: with no graphics device
there is nothing to read pixels back from and the capture is a blank image.

### `Assets/Scripts/Verification/SkipReality.cs` — The tilt optimum should sit near the top of the 10 to 30 degree band
<!-- ref:88b3 -->

Was `Assets/Scripts/Verification/SkipReality.cs:327-341`.

Loose band: the closed forms this model is a STRONGER version of only
claim "~20 deg" and [N] Fig. 4 shows a further ~15% wander with incidence
— a 10-30 deg band is generous but should still catch a force law that
is qualitatively wrong (e.g. reverted to the constant-coefficient V² form,
which has no interior optimum at all).

EXPECT THIS NEAR THE TOP OF THE BAND, and do not "fix" it by widening.
tools/physics/verify-disc-contact.py runs the same force law in a simpler
2-D, fixed-attitude, spinless form and measures the optimum at 30 deg
(incidence 10 and 15 deg) and 25 deg (incidence 20 deg) — so a reading of
28-31 here is this model agreeing with that one, not a failure. It sits
above the published 20 deg because [N] fits an extra inflection parameter
(θ̂ = 2.6) that we do not; see docs/Findings-RealSkipPhysics.md §1.4.
A reading below ~15 deg or a monotonic sweep with no interior minimum is
the real failure this check exists to catch.

### `Assets/Scripts/Verification/EventSystemVerification.cs`

#### Checks PhoneAttitude.ToUnity on all three axes headlessly
<!-- ref:5eae -->

Was `Assets/Scripts/Verification/EventSystemVerification.cs:109-123`.

The phone-space to Unity-space basis change, checked on all three axes.

Not an event-system test, and it lives here anyway because this is the only
headless C# harness in the project and the alternative is finding out on a
phone. A sign error in PhoneAttitude.ToUnity does not throw — it silently
mirrors one axis, so the stone tilts the wrong way and everything else looks
fine. That is the bug the whole rotation rework exists to remove, so it is
worth a test that runs without hardware.

The device frame is right-handed: x right across the screen, y toward the top
edge, z out through the glass. The reference pose is the phone flat, screen up,
top edge pointing at the TV.

### `controller/test-sensors.js`

#### Rotating a stationary phone must measure as zero
<!-- ref:0d59 -->

Was `controller/test-sensors.js:242-248`.

The bug this replaced. GravityFilter estimates gravity in DEVICE coordinates, so
a fast turn sweeps the gravity vector through that frame faster than the filter
tracks — and the lag reads as user acceleration that has nothing to do with how
hard the phone was thrown. Rotating a stationary phone must measure as ~zero.
A wrist snap at roughly the speed a real throw produces: 180 degrees in ~190ms,
about 900 deg/s. The phone is only turning — it is not accelerating through
space at all — so an honest measurement reads zero.

### `relay/test-dns.js`

#### Exercising the source guard for real
<!-- ref:bb33 -->

Was `relay/test-dns.js:293-303`.

Exercises the guard for real, which needs a source that is not loopback.

Loopback is unconditionally exempt so local testing works, so querying 127.0.0.1 proves
nothing about the guard — it only proves the exemption. Binding the *client* to this
machine's LAN address makes the source address a real one, and that is the path an
attacker would use.

Skipped rather than faked when there is no LAN address to bind, because a test that
silently downgrades to testing nothing is worse than one that says it did not run.

### `tools/physics/HeadlessSolver/UnityShim.cs`

#### Why a UnityEngine shim, and what pins it
<!-- ref:0e68 -->

Was `tools/physics/HeadlessSolver/UnityShim.cs:3-14`.

Minimal managed stand-ins for the handful of UnityEngine maths types the physics core
touches, so the REAL physics files compile and run on plain dotnet.

Why a shim rather than referencing UnityEngine.CoreModule.dll: several Quaternion members
(Inverse, FromToRotation, AngleAxis, Euler) are native ECalls into the Unity player. The
DLL loads fine but throws "ECall methods must be packaged into a system module" the moment
one is called. Everything the physics core needs is ordinary maths, so it is reimplemented
here from Unity's own documented formulas.

These MUST match Unity's conventions exactly or the harness would validate a solver that
is not the one that ships. `SelfTest()` checks that, and Program runs it before anything
else — see the assertions there for what "matching" is pinned to.
