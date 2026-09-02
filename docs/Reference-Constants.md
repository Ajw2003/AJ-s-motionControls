# Reference — every magic number, hidden constant, and serialized field

**2026-09-01.** A repo-wide audit and inventory. It covers the Unity runtime
(`Assets/Scripts`), the phone controller (`controller/js`), the relay (`relay/`), and every
format that gets written to a wire or a disk.

This doc is an index, not an authority. Values were read out of the code file by file; where
this doc and the code ever disagree, **the code wins** and the disagreement is a bug in this
doc. Skip physics is the one area with a deeper existing treatment —
[Reference-SkipTuning.md](Reference-SkipTuning.md) covers `SkipTuning` / `SkipConstants`
field by field and stays authoritative there; this doc does not restate it.

## How to read this — the three kinds of number

**1. Authored asset fields.** `public` fields on a `ScriptableObject` (`SkipConstants`,
`SkipTuning`, `TrailTuning`, `AimFeedbackTuning`, `RockSelectTuning`, `AudioBank`,
`RockDefinition`). These live in an `.asset` under `Assets/`, are editable in the inspector,
and change behaviour **without a recompile**. This is where a number belongs if it is a feel
decision.

**2. `[SerializeField]` fields on managers — the trap.** Bootstrap builds every manager with
`new GameObject()` + `AddComponent` (`Assets/Scripts/Managers/Bootstrap.cs`), so those
components have never been through an inspector. **Their serialized fields sit at their C#
field initializers forever.** The value in the source *is* the shipped value, and changing it
is a recompile. The project has already lost time to this twice — see the remarks on
`Assets/Scripts/EventTypes/AudioEvents/SoundId.cs` and `Assets/Scripts/Game/AimFeedbackTuning.cs`.
Treat every `[SerializeField]` listed below as "a constant that looks tunable".

**3. Bare literals** — `private const` and numbers inline in method bodies. Mostly facts
(frame rates, unit conversions, physical constants) rather than preferences.

---

## 1. Skip physics

`SkipTuning` (per rock) and `SkipConstants` (`Resources/SkipConstants.asset`) are documented
in full in [Reference-SkipTuning.md](Reference-SkipTuning.md). What that doc does **not**
cover is the private consts still inside `Assets/Scripts/Game/SkipPhysics.cs`:

| const | value | what it does |
|---|---|---|
| `OptimalAttackDeg` | 20 | The empirically optimal attack angle (Clanet, Hersen & Bocquet, 2004). A statement about what skipping *is* — deliberately not a knob; `SkipConstants.ForgivenessPitchDeg` is the knob for how forgiving missing it is. |
| `WorstQualityLossScale` | 2.5 | Multiplies energy loss at quality 0. Quality 1 pays the authored `EnergyLoss` unmodified. |
| `ShallowIncidenceLossScale` | 0.8 | Loss scaling at glancing incidence. |
| `SteepIncidenceLossScale` | 1.6 | Loss scaling at the steepest survivable incidence — a steeper arrival slaps harder. |
| `MaxLiftShare` | 0.5 | Hard ceiling on the share of surviving energy that can go upward. |
| `RollSteerRad` | 0.05 | Radians of heading change per unit `sin(roll)`. A *rotation* of horizontal velocity, never an addition. |
| `MinSkipSpeed` | 2.5 | Below this the stone sinks regardless of angle. This is the run's terminator. |
| `MinExitRiseSpeed` | 0.3 | Leave the water slower than this vertically and it did not skip. An anti-grinding guard, not the run's terminator. |

**Invariant dependency.** "A bounce always returns strictly less speed than it received"
rests on `SkipConstants.MinLoss > 0` and on lift being a *split* of the post-loss budget —
which `MaxLiftShare` and `RollSteerRad` are both written to preserve (a rotation adds no
energy; a capped share cannot exceed the budget). Changing any of the three means re-checking
the invariant first. See `docs/plans/Plan-SkipEnergyInvariant.md`.

Historical note: `RollFalloffDeg`, `MaxRollDeg`, `PitchSteepFalloffDeg`,
`PitchShallowFalloffDeg`, `YawFalloffDeg` and `MaxIncidenceDeg` used to be private consts
here and are now `SkipConstants` fields, because "what counts as a failed throw" is a feel
question. `ExitAngleDeg`, `MinimalRestitution` and `WaterLossCoefficient` are gone entirely.

---

## 2. Presentation and scene

Everything here normalises against `SkipConstants.ReferenceSpeed` (30), **not** a per-rock
max speed — there is no longer any such thing.

### `Assets/Scripts/Game/AimPreview.cs`

| name | kind | value | what it does |
|---|---|---|---|
| `previewSwingSpeed` | `[SerializeField]` | 2 m/s | Floor on the raw swing speed the arc is drawn at, so a player holding still still sees an arc. |
| `attitudeSmoothing` | `[SerializeField]` | 18 | The 20 Hz-to-smooth conversion for the ghost. Not meant to lag perceptibly. |
| `staleAfterSeconds` | `[SerializeField]` | 0.5 | Hide the preview after this long with no aim frames — surfaces a dropped phone. |
| `WaterPlaneBaseSize` | `public const` | 10 | Unity's `Plane` primitive is 10×10 units before scaling. |
| `WaterPlaneScale` | `public const` | 200 | So the drawn water is 2000 m across, not 200. |
| `WaterCentreZ` | `public const` | 60 | Water centre offset ahead of the origin at (0,0,0). |
| `WaterReachFromOrigin` | derived const | 1060 | How far drawn water runs ahead. Past this a throw still skips — `SkipPhysics` bounces against the `WaterY` **plane equation**, not the mesh — but over water nobody can see. |

Uncommented scene literals in the same file's placeholder-scene builder: line width `0.12`,
camera `farClipPlane` 1000, `fogStartDistance` 120 / `fogEndDistance` 600, water colour
`(0.06, 0.16, 0.28)`, horizon `(0.05, 0.09, 0.15)`. All placeholder-art values that an
authored lake scene is expected to replace (M2, per `docs/DOCUMENTATION_AUDIT.md`).

### `Assets/Scripts/Game/RockController.cs`

| name | kind | value | what it does |
|---|---|---|---|
| `underwaterSpinDamping` | `[SerializeField]` | 0.8 s | Water-drag time constant for spin after the stone is under. The only visual-only spin number left; everything else is `SkipConstants.SpinRetention`. |
| `despawnDelay` | `[SerializeField]` | 2.5 s | |
| `fastSinkDuration` | `[SerializeField]` | 0.45 s | A hard arrival goes under quickly. |
| `slowSinkDuration` | `[SerializeField]` | 1 s | A spent run settles rather than knifing, and needs longer on screen to read as settling. |
| `fastSinkDepth` | `[SerializeField]` | 2.2 m | Only needs to beat the point where water occludes the stone. |
| `slowSinkDepth` | `[SerializeField]` | 0.9 m | |
| `settleHold` | `[SerializeField]` | 1.2 s | Beat held before the turn may advance. Without it the score banks ~16 ms after the stone touches water — the difference between a throw that *ended* and one that glitched. |
| `MaxVisualSpinDegPerSec` | `internal const` | 1200 | Ceiling on drawn spin. Not taste — it is the frame rate: a flat box's silhouette repeats every half turn, so past ~2700 deg/s at 60 fps it wagon-wheels. Real flicks measure 1500–2500 deg/s, so this is reachable in play. Clamped, not rescaled, so below the ceiling the stone turns at exactly the measured rate. Shared with the aim ghost. |

### `Assets/Scripts/Game/WaterRipples.cs`

| name | kind | value | what it does |
|---|---|---|---|
| `MaxRipples` | `public const` | 16 | **Must match `MAX_RIPPLES` in `WaterRipple.hlsl`** (and `WaterRipple.shader` while it lasts). A cross-file duplicate with no compiler check. |
| `rippleLifeSeconds` | `[SerializeField]` | 2.5 | Seconds a ring takes to fade out. |
| `rippleSpeed` | `[SerializeField]` | 6 | Leading-edge expansion, world units/s. |
| `minImpactSpeedFraction` | `[SerializeField]` | 0.15 | Faintest ring, as a fraction of `ReferenceSpeed`. |
| `maxImpactSpeedFraction` | `[SerializeField]` | 0.96 | Strongest ring, same basis. |

The fractions exist because these were once hardcoded at 4 and 25 against a fixed `MaxSpeed`
of 26 — raise launch speed and every impact clamps to full strength, so the ripple stops
responding to anything. `_minImpactSpeed = 4f` / `_maxImpactSpeed = 25f` remain in the source
as the pre-resolution seeds.

### Tuning assets — `TrailTuning`, `AimFeedbackTuning`, `RockSelectTuning`

Kind 1 (authored assets), all `[Range]`-bounded, all self-documenting via `[Tooltip]`.
Defaults for reference:

- **`TrailTuning`** — `Time` 0.55, `WidthMultiplier` 0.18, `MinVertexDistance` 0.15,
  `MinSpeedFraction` 0.15 / `MaxSpeedFraction` 0.96 (same `ReferenceSpeed` basis as the
  ripples), `SlowTint` `(0.55, 0.62, 0.70, 0.55)`, `FastTint` white, `WakeWidthScale` 4,
  `WakeAlphaScale` 0.35, `DashLength` 0.6, `DashDutyCycle` 0.5.
- **`AimFeedbackTuning`** — the arc's red/amber/green thresholds only: `PoorSkips` 2,
  `GoodSkips` 9; `PoorColour`/`FairColour`/`GoodColour`; `TailAlpha` 0.15,
  `ColourSmoothing` 7. The ghost's own charge fields (`IdleScale`/`ChargedScale`,
  `IdleColour`/`ChargedColour`/`ChargedEmission`, `PulseHz`/`PulseAmount`) are gone — the
  ghost now shows the aiming player's colour instead of swelling and glowing with wind-up,
  via `AimPreview.TintGhost` and `PlayerColours.StoneColourFor`.
- **`RockSelectTuning`** — `FanDegrees` 38, `ShoreRadius` 6, `ShoreHeight` 0,
  `StickyMarginDeg` 3, `PointerSmoothing` 12, `PointerStaleSeconds` 1.5,
  `HighlightScale` 1.12, `HighlightColour`, `ConfirmedColour`.

### `Assets/Scripts/Game/PlayerColourPalette.cs` — player colours

`Resources/PlayerColourPalette.asset`, an authored asset on the `SkipConstants` pattern.
Owns the eight default swatches plus the rules the host applies to a colour a phone picks
for itself:

| field | value | what it does |
|---|---|---|
| `Swatches` | 8 colours | Handed out furthest-from-everyone-first as players join, and sent to phones as the picker's quick-picks. |
| `MinSeparation` | 0.08 | How far apart two connected players' colours must be in HSV. A closer pick is rotated in hue, not refused. Set below the palette's own closest pair (yellow/orange, 0.094) so every swatch is a legal pick; a higher value would rotate a player off the swatch they just tapped. 0 disables. |
| `MinValue` | 0.45 | Brightness floor — below it a dot reads as a hole in the water. |
| `MinSaturation` | 0.25 | Saturation floor, gentle so white stays a legitimate choice. |
| `ConfirmedBlend` | 0.65 | How far a claimed stone is pulled toward its owner's colour. The pointed-at-but-unclaimed stone stays on `RockSelectTuning.HighlightColour` — a highlight belongs to whoever is pointing, not to a player. |

These are read by `Managers.PlayerColours`, which is the only source of truth for a player's
colour. This replaced a `static readonly Color[8]` in `ShoreRocks` indexed by
`hash(playerId) + <pointers alive when this one was created>` — an ordinal that reset on every
phase change, so colours moved between rounds and collided freely. Both `notes.txt` colour
complaints were that one expression.

### `Assets/Scripts/Game/ShoreRocks.cs`

Hardcoded here: label rect `(-80, -20, 160, 24)` and the blurb/tick text colours.

---

## 3. Phone controller (`controller/js`)

### Sensors — `sensors.js`

| const | value | notes |
|---|---|---|
| `SAMPLE_HZ` | 60 | **Sample rate, not poll rate.** Release detection watches for a sharp stop; polling at the 20 Hz aim cadence would miss it by up to 50 ms and read orientation after the phone had already stopped. |
| `BUFFER_SECONDS` | 1.5 | Ring buffer depth — `ceil(SAMPLE_HZ * BUFFER_SECONDS)` = 90 samples. |
| `GRAVITY_MS2` | 9.80665 | Standard gravity, for converting reported acceleration. |
| `gravityK` default | 0.8 | Low-pass constant for the gravity estimate. Overridable per-session from the URL (`?gravityK=0.92`) so it can be dialled in on a real device — `app.js` reads it into `GRAVITY_K`. |
| `GravityFilter` `tauMs` | 75 | Constructor default; values <1 are reinterpreted as a per-sample coefficient. |
| `MAX_SAMPLE_GAP_MS` | 250 | Longest gap still worth integrating across. A longer one means the browser stopped delivering events (backgrounded, screen locked, tab throttled) and the motion was never measured — integrating across it invents a swing out of a pause. Exported from `sensors.js` and used at all four sites; it used to be a bare literal in each, three lines from an unrelated `spinEnvelopeTauMs: 250`. |

### Throw gesture — `throw.js` (`ThrowTuning`)

| field | value | what it does |
|---|---|---|
| `swingAccel` | 14 | Acceleration that counts as a real swing rather than hand tremor. |
| `stopAccel` | 4 | Once swinging, falling below this counts as stopping. |
| `stopSamples` | 3 | Consecutive calm samples needed to call a stop. One is not enough — a swing passes through zero acceleration at its own turnaround and would fire mid-arc. |
| `maxSwingMs` | 900 | Throw anyway this long after the peak, rather than leaving the player armed forever. |
| `spinEnvelopeTauMs` | 250 | Decay on the held spin peak. Spin is peaky — a wrist flick is over in ~100 ms, so a 20 Hz aim frame catches mostly the tail. Holding the peak is what makes the ghost spin up and wind down instead of flicker. The throw uses the same envelope, so the stone spins at the rate the ghost showed. |

`aimHz` — 20, a `ThrowController` constructor default (`this.aimIntervalMs = 1000 / aimHz`).
This is the number the relay rate limit and `AimMessage`'s "~20Hz" doc comment are both sized
against; see §6.

`SimulatedThrow` — the desktop-seat fallback, so a PC can occupy a seat in multi-player
testing (without it, a seat that cannot throw deadlocks the match, since the turn only
advances on a completed throw): `minHoldMs` 150, `maxHoldMs` 1200, `minSwingSpeed` 1.5,
`maxSwingSpeed` 6, `pitchJitterDeg` 6, `yawJitterDeg` 10, `spinRate` 900 (visual only).
Jitter stays well inside the tolerance window so simulated throws skip rather than all
scoring zero.

`IDENTITY_ATTITUDE` = `{w:1, x:0, y:0, z:0}` — a flat stone facing forward. The ~20° the
water wants is applied inside the bounce, never to the stone.

### Connection — `net.js`

| const | value | what it does |
|---|---|---|
| `REJOIN_WINDOW_MS` | 45 000 | How long to keep trying to get back into the same room. Sized against the relay's `HOST_GRACE_MS`; giving up on the first refusal is what made a two-second hiccup look like the game had ended. |
| `REJOIN_RETRY_MS` | 1500 | Retry cadence inside that window. |
| `BACKOFF_START_MS` | 500 | |
| `BACKOFF_MAX_MS` | 8000 | |

`PEER_ENABLED` (`app.js`) — WebRTC direct path, on unless `?direct=0`.

---

## 4. Relay (`relay/`)

`relay/server.js`. Env-overridable ones are marked.

| const | value | env | what it does |
|---|---|---|---|
| `PORT` | 3000 | `PORT` | |
| `BIND_HOST` | `0.0.0.0` | `BIND_HOST` | Must default to all interfaces. Behind a reverse proxy on the same host, set `127.0.0.1` so the plain-HTTP relay is not reachable from the LAN alongside the TLS one. |
| `MAX_PLAYERS_PER_ROOM` | 8 | — | Also the length of `ShoreRocks.PointerPalette`. |
| `MAX_MESSAGE_BYTES` | 4096 | — | |
| `HEARTBEAT_MS` | 30 000 | — | |
| `ROOM_IDLE_TIMEOUT_MS` | 4 h | — | |
| `ROOM_SWEEP_MS` | 5 min | — | |
| `HOST_GRACE_MS` | 60 000 | `HOST_GRACE_MS` | How long a room outlives its host, so a redeploy or Wi-Fi blip does not eject everyone and invalidate the code. Overridable so tests need not wait a minute. |
| `HOST_GRACE_SWEEP_MS` | 2 000 | — | |
| `RATE_WINDOW_MS` | 1000 | — | |
| `RATE_MAX_MESSAGES` | 60 | — | AIM streams at 20 Hz; leaves headroom for bursts. |
| `CODE_ALPHABET` | `ABCDEFGHJKLMNPQRSTUVWXYZ` | — | **No I and no O** — codes are read aloud off a TV and typed on a phone. |
| `CODE_LENGTH` | 4 | — | |
| `MAX_PLAYER_ID_LENGTH` | 16 | — | |
| `MAX_QR_DATA_LENGTH` | 512 | — | A join URL is well under this; the endpoint is reachable through the public tunnel, so it bounds rather than trusts the caller. |
| `DNS_SUFFIX` | `.local-ip.sh` | `DNS_SUFFIX` | Plus `DNS_RESPOND_IP`, `DNS_ALLOW`, `DNS_UPSTREAM`, `TLS_CERT`, `TLS_KEY` — all env-only. |

Static file serving: `STATIC_DIR` resolves to `../controller` relative to `relay/`, and
`CONTENT_TYPES` is the extension→MIME map — a file type missing from it is served as a
download rather than a page.

`relay/dns.js` — protocol constants, fixed by RFC 1035, not tunable: `TYPE_A` 1, `CLASS_IN` 1,
`RCODE_NOERROR` 0, `RCODE_NXDOMAIN` 3, `RCODE_REFUSED` 5, and the `TYPE_NAMES` map.

Test/tool ports, deliberately off the real ones: `relay/test-relay.js` 3999,
`relay/test-dns.js` 5390, `relay/loadtest.js` default `ws://127.0.0.1:3100` with
25 rooms × 4 players × 15 s.

---

## 5. Serialized formats — the compatibility contracts

These are the values that cannot be changed unilaterally: something on the other side of a
wire, a disk, or an authored asset agrees with them today.

### 5.1 Wire protocol (`Assets/Scripts/Net/Messages.cs`)

Every inbound message is deliberately **flat**, so Unity's built-in `JsonUtility` can parse it
with no Newtonsoft dependency — which also forces public fields rather than properties.
`JsonUtility` **silently ignores unknown keys**, so adding a field is backward-compatible and
removing one is not loud.

`MessageType` string constants: `CREATE_ROOM`, `ROOM_CREATED`, `PLAYER_JOINED`,
`PLAYER_LEFT`, `AIM`, `THROW`, `PICK_ROCK`, `PICK_COLOUR`, `ADVANCE`, `TURN`, `SPECTATE`, `ERROR`, and the
signalling trio `RTC_OFFER`, `RTC_ANSWER`, `RTC_ICE`. The relay routes the RTC three without
understanding them — player→host stamped with `playerId`, host→player via `targetPlayerId` —
which is why adding peer connections needed no relay change.

| type | fields |
|---|---|
| `MessageEnvelope` | `type` — peeked first to pick the concrete type |
| `RoomCreatedMessage` | `roomCode`, `resumed` (relay still held the room across the drop), `players[]` |
| `PlayerJoinedMessage` | `playerId`, `reconnected` |
| `PlayerLeftMessage` | `playerId` |
| `ErrorMessage` | `code`, `message` |
| `AimMessage` | `playerId`, `seq`, `qw`/`qx`/`qy`/`qz`, `spinRate`, `swingSpeed` |
| `PickRockMessage` | `playerId` only |
| `PickColourMessage` | `playerId`, `colour` as `"#rrggbb"` — the format `<input type="color">` produces and CSS consumes, so the phone never converts and no float formatting can cross a culture. Unlike `PICK_ROCK` it carries its payload: the TV owns the shore highlight, but it cannot know what colour a player wants. A request, not an instruction — `PlayerColours.Pick` decides what they get. |
| `AdvanceMessage` | `playerId` only |
| `ThrowMessage` | `playerId`, `swingSpeed`, `qw`/`qx`/`qy`/`qz`, `spinRate`, `sampleCount` |

Three design constraints worth not breaking:

- **Attitude is four floats, not three angles.** The phone used to decompose into
  aim/attack/roll and Unity re-composed it with two hand-found sign flips. A quaternion has no
  decomposition to get wrong and no degenerate pose. Identity = "however they were holding it
  at the tap". It stays in the phone's right-handed frame on the wire; `Game.PhoneAttitude.ToUnity`
  is the only thing permitted to convert it.
- **`swingSpeed` is raw m/s, never normalised.** The single conversion is
  `SkipConstants.SpeedScale`, in `SkipPhysics.CreateLaunch`. An earlier version sent a 0..1
  `power`, which meant two lossy conversions of one swing had to agree on both sides or the
  arc and the stone disagreed by construction.
- **`PICK_ROCK` carries no rock id and `ADVANCE` carries no target phase.** The TV owns the
  shore layout and the phase; a tap only means "confirm whatever that is".

Outbound, hand-written in `OutboundMessages` because `JsonUtility` cannot serialise a
dictionary:

- `BuildTurn` → `{type, targetPlayerId, yourTurn}`.
- `BuildSpectate` → `{type, activePlayer, rockName, phase, round, totalRounds, choosing,
  lastPlayer, lastSkips, lastDistance, scores:{playerId:int}, rocks:{playerId:{rockId, rockName}},
  colours:{playerId:"#rrggbb"}, palette:["#rrggbb", …]}`. `colours` is every player's settled
  colour, so a phone can paint its own accent and tell the others apart in the scores;
  `palette` is the picker's quick-picks, sent rather than duplicated in the controller so the
  swatches are authored in one place.
  `phase` is `LOBBY` / `PLAYING` / `RESULTS`; a mid-match re-pick reports `LOBBY` with
  `choosing:true`. `lastPlayer` is named separately from `activePlayer` because the turn has
  already advanced by the time this goes out. Sent on state changes only, **never on a timer**
  — it used to go out 4×/s for the whole flight and was the most expensive thing on the wire.
  `lastDistance` is formatted `F2` with `InvariantCulture`; player-supplied strings go through
  a minimal `Escape`.

### 5.2 WebRTC signalling (`Assets/Scripts/Net/PeerLink.cs`)

Private `[Serializable]` shapes: `SdpIn {playerId, sdp}`, `AnswerOut {type, targetPlayerId, sdp}`,
`IceIn {playerId, candidate, sdpMid, sdpMLineIndex}`, `IceOut {type, targetPlayerId, …}`.
`establishTimeout` 8 s.

### 5.3 On-disk state, beside the executable

All of these follow one deliberate rule: **not `PlayerPrefs`.** On Windows that writes into
the registry — invisible, not cleared by deleting the game folder, not inspectable or
editable by hand. Path resolution is identical in all cases:
`Path.Combine(Application.dataPath, "..")`, which is the repo root in the Editor and
`<exe>_Data/..` in a build.

| file | shape | notes |
|---|---|---|
| `match-settings.json` | `{randomRocks: bool, roundsPerMatch: int}` | `roundsPerMatch` defaults 5, **clamped 1–10 on load**. A stale `tuningSet` key from the retired Authored/Derived comparison parses harmlessly and is dropped on the next Save. Falls back to shipped defaults on any read/parse failure. |
| `network-mode.txt` | one line: `Lan` or `Hotspot` | Parsed case-insensitively; falls back to `Lan` on any failure. Read fresh every time StartScreen shows. |
| `relay-url.txt` | one line, e.g. `ws://localhost:3000` | Overrides `RelayClient.relayUrl` so a shipped build can be repointed without a rebuild. |
| `relay-env.txt` | env lines | Gitignored. Written by `deploy/local-https.ps1`; absent otherwise. |

Health/IPC payloads: `LocalServices.HealthPayload {ok, dns:{enabled, bound}}` (the nested
`bound` exists because HTTP success only proves the listener is up, not that the DNS responder
bound) and `HotspotService.ScriptResult {ok, ssid, passphrase, warning, error, message}`.

### 5.4 Asset-serialized identity — ordinals matter

- **`SoundId`** — the enum is the sound's identity, and gameplay code holds no `AudioClip` at
  all. But an enum's *ordinal* is what Unity writes into the `AudioBank` asset, so
  **reordering or inserting a member silently remaps every authored entry.** Append only.
  Members today: `Skip`, `SkipMilestone`, `Sink`, `ThrowRelease`, `ScoreTick`, `ThrowScored`,
  `ThrowFailed`, `TurnStart`, `MatchStart`, `MatchWin`, `MatchTie`, `PlayerJoined`, `UiConfirm`.
  Same caution applies to `AudioChannel` and `NetworkMode` (though `NetworkMode` persists by
  *name*, so it is safe to reorder).
- **`AudioBank.Entry`** — `Volume` 1, `BasePitch` 1, `PitchJitter` 0.03, `SemitonesPerStep` 0,
  `StepCap` 14, `Channel` `Sfx`, `Spatial` true, `MinDistance` 5, `MaxDistance` 150.
- **`RockDefinition`** — `Id` `"rock"`, `DisplayName` `"Pebble"`, `Blurb` empty,
  `LocalScale` one, `MeshRotation` zero, `Tint` `(0.45, 0.43, 0.43)`, plus `Mesh` and a
  `SkipTuning` reference; `IsUsable` requires both non-null. `Id` is the string that travels
  in the SPECTATE `rocks` map.
- **Settings keys** — `MatchSettings` registers `"randomRocks"` and `"roundsPerMatch"`; these
  match the `match-settings.json` field names and should stay in step.

---

## 6. Cross-surface duplicates — values that must agree

Nothing enforces any of these. This is the list to check when one side changes.

| value | where it lives | risk if they drift |
|---|---|---|
| Gravity | `SkipConstants.Gravity` 9.81 (sim) vs `sensors.js GRAVITY_MS2` 9.80665 (phone) | Low and intentional — one is game feel, one is a measurement conversion. Noted so nobody "fixes" one to match the other. |
| Aim rate, 20 Hz | `throw.js` `aimHz` default; `sensors.js` comments; `AimMessage` doc comment; `AimPreview.attitudeSmoothing`; relay `RATE_MAX_MESSAGES` 60/s | Raising `aimHz` past ~60/s trips the relay rate limiter. `attitudeSmoothing` is sized for 20 Hz. |
| Room capacity, 8 | relay `MAX_PLAYERS_PER_ROOM`; `ShoreRocks.PointerPalette.Length` | A ninth player would index past the palette. |
| Relay port 3000 | `LocalServices.relayPort`; `relay/server.js PORT`; the `relay-url.txt` example | The override file's port wins over `relayPort`. |
| HTTPS port 8443 | `LocalServices.securePort`; `deploy/local-https.ps1` | |
| Ripple count 16 | `WaterRipples.MaxRipples`; `MAX_RIPPLES` in `Assets/Resources/WaterRipple.hlsl:40` and `WaterRipple.shader:76` | Shader array overrun / silently dropped ripples. |
| Reconnect window | `net.js REJOIN_WINDOW_MS` 45 s vs relay `HOST_GRACE_MS` 60 s | The phone must not give up before the relay releases the room. Currently correctly ordered, with 15 s of margin. |
| DNS suffix `.local-ip.sh` | `CertificateProvider.Suffix`; `relay/server.js DNS_SUFFIX` | The cert is issued for the name the DNS responder answers; a mismatch fails TLS on the phone. |
| Nominal hard throw, 30 m/s | `SkipConstants.ReferenceSpeed`; `SkipCurve.ReferenceSwingSpeed` | The acceptance harness's pass band is measured at this speed. |
| Rounds per match, 5 | `TurnManager.roundsPerMatch`; `MatchSettingsStore.SettingsData.roundsPerMatch`; the `MatchSettings` getter fallback | Three copies of one default. |
| Speed normalisation 0.15 / 0.96 | `WaterRipples`, `TrailTuning`, `SfxDirector` | Three systems independently authored to the same band against `ReferenceSpeed`. |

---

## 7. Match, scoring, audio

`[SerializeField]` on Bootstrap-created managers — kind 2, so these are effectively constants:

- **`TurnManager`** — `roundsPerMatch` 5 (overridden at Awake from `match-settings.json`),
  `reChooseEveryRounds` 3, and the host keyboard bindings `startMatchKey` Space,
  `nextTurnKey` N, `forceChooseKey` C. Space runs the same switch as an `ADVANCE` from a
  phone, so keyboard and phones cannot advance the match to different places.
- **`ScoreManager`** — `pointsPerSkip` 10, `pointsPerMetre` 1. (README mentions a `pointsPerRing`;
  no such field exists in `ScoreManager` and nothing publishes `RingPassedEvent` — M2 scope.)
- **`RockSelection`** — `randomRocks` false, overridden at Awake from `match-settings.json`.
- **`AudioManager`** — `voiceCount` 12, `mixerResourcePath` `"GameMixer"` (a
  `Resources.Load` path, so renaming the mixer asset breaks it silently).
- **`SfxDirector`** — `minImpactSpeedFraction` 0.15 / `maxImpactSpeedFraction` 0.96,
  `milestones` `{5, 10, 15, 20}` (the runs that get a `SkipMilestone` layer), `scoreDelay`
  0.35 s, `turnDelay` 1.2 s.
- **`RelayDebugHud`** — `visible` true, `historySize` 6, `toggleKey` F1.
- **`HotspotService`** — `startTimeout` 30 s, `stopTimeout` 3 s, 0.1 s poll.
- **`LocalServices`** — `launchOnStart` true, `useHttps` true, `dependencyInstallTimeout`
  180 s, `relayStartTimeout` 30 s, 0.25 s health-poll interval.
- **`Singleton`** — `persistBetweenScenes` false, `detachFromParent` true.

`tools/audio/generate-sfx.js` — `SAMPLE_RATE` 48000, plus a per-sound `CATALOGUE`.

---

## 8. Host UI, QR, certificates, and tooling

### UI screens (`Assets/Scripts/UI`)

All host-side keyboard bindings, all `[SerializeField]` (kind 2 — the initializer is the
shipped value):

| screen | bindings and fields |
|---|---|
| `JoinScreen` | `visible` true, `toggleKey` F2, `compactDuringMatch` true (shrink to a corner card once the match starts, so the lake is visible but latecomers can still scan), `quietZoneModules` 4 — **below 4 many scanners fail to lock on**; see [Rule-RenderQR.md](Rule-RenderQR.md). QR fetch backoff: `QrRetryBaseSeconds` 0.5, doubling to `QrRetryMaxSeconds` 5 (the QR endpoint is unreachable for a moment on every hotspot start, since `PublicOrigin` points at 192.168.137.1 before tethering is up). |
| `ResultsScreen` | `toggleKey`, `visible` |
| `MatchSettingsScreen` | `toggleKey` |
| `StartScreen` | `lanKey`, `hotspotKey`, `startKey` |
| `RelayDebugHud` | `toggleKey` F1 |

`TurnManager` owns Space / N / C (see §7). These are all distinct by hand — nothing checks
for a collision, so a new binding has to be diffed against this table.

### `RelayClient` reconnection

`OverrideFileName` `relay-url.txt`, `MaxRetryDelay` 4 s, `ConnectTimeout` 8 s,
`ConnectWatchdog` = `ConnectTimeout + 5` = 13 s. The watchdog is deliberately derived from
the timeout rather than written out, so the two cannot drift.

### `CertificateProvider` — local HTTPS

| const | value | notes |
|---|---|---|
| `CertUrl` / `KeyUrl` | `https://local-ip.sh/server.pem` / `.key` | An external dependency of the offline setup. |
| `Suffix` | `.local-ip.sh` | **Duplicates `DNS_SUFFIX` in `relay/server.js`** — add to the §6 list when either changes. |
| `CacheDirName` | `local-https` | |
| `RenewWithinDays` | 14 | Refetch when the cached cert expires inside this window. |
| `TimeoutSeconds` | 20 | |

### `HotspotService`

`Alphabet` — `ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789`, used to generate the
hotspot passphrase. Same no-I/O/l/0/1 principle as the relay's room-code alphabet, but a
separate string with its own case and digit rules; the two are not interchangeable.

### `ChildProcessGroup`

`JobObjectExtendedLimitInformationClass` 9 and `JobObjectLimitKillOnJobClose` `0x2000` are
Win32 API constants, fixed by the platform. Not tunable.

### Verification and telemetry

- **`RockTelemetry`** — writes `rock-throws.csv` beside the executable (same path resolution
  as §5.3), with a fixed `Header`:
  `timestamp,matchSeed,rockId,player,swingSpeed,spinRate,pitchDeg,rollDeg,yawDeg,skips,distance`.
  This is a serialized format: `docs/archive/rock-throws-old-model-2026-08-28.csv` is a
  snapshot under the previous physics model, so changing columns orphans older captures.
- **`SkipCurve`** — the acceptance harness. `ReferenceSwingSpeed` 30 (the same nominal hard
  throw as `SkipConstants.ReferenceSpeed`), `ReferenceSpinRate` 400 (matches
  `SkipConstants.SpinForStability`), `TerminationBudgetSeconds` 120 (a run exceeding this is
  a failed invariant, not a long throw), and the pass band `MinGoodSkips` 15 /
  `MaxGoodSkips` 30 — the window `EnergyLoss` is tuned to land inside.

### Editor builders (`Assets/Scripts/Editor`)

Hardcoded asset paths — these are build-time contracts with the project layout, and a moved
asset breaks them silently:

- `AudioBankBuilder` — `ClipFolder` `Assets/Audio`, `BankPath` `Assets/Resources/AudioBank.asset`.
- `RockCatalogBuilder` — `FbxPath` `Assets/Resources/Fbx/RocksRemeshed.fbx`,
  `PrefabPath` `Assets/Resources/RockObject.prefab`, `RockFolder` `Assets/Rocks`,
  `CatalogPath` `Assets/Resources/RockCatalog.asset`, plus `FallbackFootprint` 0.75 and
  `FallbackFlatten` 0.52 for a mesh whose bounds cannot be measured.

Runtime `Resources.Load` paths are the same kind of hidden string: `"SkipConstants"`,
`"RockCatalog"`, `"AudioBank"`, and `AudioManager.mixerResourcePath` `"GameMixer"`.

### `Bootstrap` feature flags

`Assets/Scripts/Managers/Bootstrap.cs` carries a block of `[SerializeField] bool` switches
that decide what exists at runtime: `autoLoadFirstScene`, `eventDebugLogging`,
`startLocalServices`, `showJoinScreen`, `showResultsScreen`, `showMatchSettingsScreen`,
`recordRockTelemetry`, `showDebugHud`, `showAimPreview`, `enableAudio`, `usePeerConnections`,
plus `firstScene`. Kind 2 — the source initializer is the shipped configuration, so this is
the closest thing the project has to a build config file.

Also uncatalogued elsewhere: `RockLauncher.cameraFollowSmoothing` 0.25,
`AimPreview.createPlaceholderScene` and its `tuning` / `constants` / `feedback` /
`throwOrigin` asset references (null in practice — `Awake` loads from `Resources` instead,
which is exactly the kind-2 trap in §"How to read this").

---

## 9. Gaps — constants with no rationale recorded anywhere

Carry-over work; none of these are broken today.

1. **`AimPreview` placeholder-scene literals** (fog, clip plane, colours, line width) —
   uncommented, and expected to be superseded by an authored lake scene.
2. **`AudioManager.voiceCount` 12** — no note on how the number was chosen or what happens on
   voice starvation.
3. **`AudioBank.Entry.StepCap` 14** and `SemitonesPerStep` — no recorded reasoning for 14.
4. **`RockSelectTuning.ShoreRadius` 6 / `FanDegrees` 38** — geometry with no note on the
   camera framing they were chosen against.
### Closed by the fixes in this pass

- ~~`RockLauncher.cs:201` tinted a thrown stone from `RockDefinition.Tint` alone~~ — it now
  calls `PlayerColours.StoneColourFor`, the same blend the shore stone and the ghost use.
  The trail (`RockTrail.ApplySpeedTint`) multiplies in the player's full colour on top of
  its existing speed fade, so a dying run still dims. The pre-release ghost is tinted the
  same way — see `AimPreview.TintGhost` — which meant removing the ghost's separate
  swell/glow "charge" feature (`AimFeedbackTuning`'s `ChargeGlow` block) entirely, since
  both used the ghost's material colour for different things.

- ~~`RelayClient.relayUrl` pointed at the torn-down Fly.io host~~ — now defaults to
  `ws://localhost:3000`, matching `LocalServices.relayPort`, and the last-resort branch logs
  a warning saying phones cannot reach it.
- ~~The 250 ms sample-gap clamp was an unnamed literal in four places~~ — now
  `sensors.js`'s exported `MAX_SAMPLE_GAP_MS`.
- ~~`ShoreRocks.PointerPalette` indexed by ordinal~~ — colours now belong to
  `Managers.PlayerColours`, keyed by player identity, picked on the phone, with the palette
  on `Resources/PlayerColourPalette.asset`.
- ~~`SoundId.SkipMilestone` said "5, 10, 15" against a `{5,10,15,20}` array~~ — the comment
  now points at `SfxDirector.milestones` instead of restating it.
- ~~`Assets/CLAUDE.md` documented retired per-rock constants and two removed menu items~~ —
  its rock-tuning section and menu table now describe the current model.

### Not a gap after all

`AudioBankBuilder.ClipFolder` → `Assets/Audio`. I recorded this as a missing folder; it is
**generated by design**. `tools/audio/generate-sfx.js` writes there by default, `.gitignore`
lists every file it produces, and `AudioBankBuilder.Build` already fails with "run
`node tools/audio/generate-sfx.js` from the repo root first". Run that before the Rebuild
Audio Bank menu item on a fresh clone.
