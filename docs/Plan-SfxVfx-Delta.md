# Plan-SfxVfx.md vs. the code as it stands (`urp-migration`, 2026-08-25)

Audit only. No source changed. Read alongside [`Plan-SfxVfx.md`](Plan-SfxVfx.md).

> **Superseded as a status report, later on 2026-08-25.** This is a snapshot of the moment it was
> written, and it is kept as one — do not read the tables below as current. Since then M1–M4 all
> landed (AudioManager, the six events, core SFX, trails), placeholder clips were generated into
> `Assets/Audio/`, and §6.1 and §6.2 were built. The *reasoning* here — the URP consequences, the
> depth-write window, the queue argument — is all still live. Only the "nothing is built" tally is
> out of date. `Plan-SfxVfx.md` §8 carries the current status.

## Headline: §1.1 is now wrong, and it was the plan's biggest constraint

The plan was written against Built-in and says in bold **"Do not migrate to URP for this work."**
The migration happened anyway, on this branch:

- `Packages/manifest.json` → `com.unity.render-pipelines.universal: 17.3.0`
- `Assets/UniversalRenderPipelineGlobalSettings.asset`, `Resources/RockSkippingUrp.asset`,
  `RockSkippingUrp_Renderer.asset` all exist
- The water was rewritten as `Resources/WaterRippleURP.shadergraph` (+ `WaterRipple.hlsl`);
  the Built-in surface shader survives only as a runtime fallback in
  [`PlaceholderArt.CreateWater`](../Assets/Scripts/Game/PlaceholderArt.cs#L118)

The feared cost (rewriting the water) was already paid. So the §1.1 table inverts:

| §1.1 said | Now |
|---|---|
| VFX Graph — No | **Available** (needs `com.unity.visualeffectgraph` added) |
| Shader Graph — No | **Available and already in use** |
| URP `Volume` post-processing (bloom, vignette) | **Available** — §6.11 "deferred, needs PPv2" is dead; bloom is a Volume component now |
| Post Processing Stack v2 | Do **not** add — wrong stack for URP |

**Open question 6 ("PPv2 in or out?") is void.** Replace it with: add a Global Volume with Bloom
to the placeholder scene, yes or no.

Whether to also add VFX Graph is a real new decision. Shuriken (§6.5) still works and is the
lower-risk choice for a handful of small bursts; VFX Graph earns its keep only at particle counts
this game does not have.

## §1.2 — render queues: still true, and the plan's queue list needs revising

`PlaceholderArt` still pins queues by hand, but only three constants exist
([lines 39–45](../Assets/Scripts/Game/PlaceholderArt.cs#L39)): `WaterQueue = 2000`,
`StoneQueue = 3100`, `ArcQueue = 3200`. The plan's proposed `TrailQueue = 3050`,
`SplashQueue = 3060`, `WorldUiQueue = 3300` are **not** added. That work still stands.

But the *reasoning* changed, and the file now documents it at length:

- Post-URP the failure mode is **occlusion, not draw order**. The comment records two dead ends —
  a later queue does not beat `ZTest LEqual`, and `_ZTest` on the stone is inert because URP's Lit
  has no such property. The only lever is `SetWaterDepthWrite` on the water, which *is* a Shader
  Graph material with override enabled.
- [`WaterRipples`](../Assets/Scripts/Game/WaterRipples.cs#L73) now toggles water depth-write **on
  for the length of a sink** so the lake can swallow the stone.

Consequences for the VFX layer the plan could not have known:

1. **§1.2's "soft particles will not work, the water writes no depth" is no longer categorically
   true** — depth write is now a runtime toggle that flips mid-throw. Soft particles would fade
   against the water during a sink and not otherwise. That's worse than either state. Stick with
   hard-edged particles, but for the new reason.
2. **A trail or splash spawned during a sink can be clipped by the water**, at the exact moment
   §1.2 warned about. Any new renderer needs deciding against the depth-write window, not just a
   queue number.
3. URP also gives a real depth texture via the renderer asset — worth checking whether
   `RockSkippingUrp_Renderer.asset` has Depth Texture enabled before relying on any depth-based
   effect.

## §1.3 / §1.4 / §7 — unchanged and still accurate

- `Bootstrap.Awake` still builds everything with `new GameObject()` + `AddComponent`
  ([Bootstrap.cs:44](../Assets/Scripts/Managers/Bootstrap.cs#L44)), and the subscription-order
  comment (`ScoreManager` before `TurnManager`) is intact — §4.1 holds.
- `autoLoadFirstScene` still defaults false; everything still runs in the Bootstrap scene, so
  **music off `MatchPhase`, not scene loads** (§1.4) is still correct.
- `RockLauncher.CreateRockObject` still `Resources.Load`s `RockObject.prefab`
  ([line 150](../Assets/Scripts/Game/RockLauncher.cs#L150)) — the §1.3(a) prefab precedent for
  `AudioManager.prefab` holds.
- The pure-solver rule, the fraction-of-`MaxSpeed` rule, and the OnDestroy rule are all still
  enforced in the code they were quoted from.

## Implementation status: nothing from the plan is built

| Plan item | Status |
|---|---|
| M1 AudioManager / `SoundId` / `PlaySoundEvent` / `AudioBank` / mixer / source pool | **None.** No `Scripts/Managers/AudioManager.cs`, no `EventTypes/AudioEvents/`, no `.mixer`, and still **zero** `.wav`/`.mp3`/`.ogg` anywhere in `Assets/` |
| M2 six new events (§4) | **None.** `EventTypes/` holds only `SkipOccurredEvent`, `ThrowCompletedEvent`, `PlayerJoined/Left`, `PlayerStanding` |
| M3 core SFX | None |
| M4 trails (`TrailTuning`, `TrailQueue`, `TrailRenderer`) | None — no `TrailRenderer` reference in `RockLauncher` |
| M5 music / M6 scheduling / M7 polish | None |

Art moved instead: new rock models (`Resources/Fbx`, `RocksRemeshed.prefab`, `rock.mat`) and rock
scaling stripped from the codebase, per the last three commits.

**Open question 1 (where do the audio assets come from?) is now the gating question**, not a
footnote. It has been open since the plan was written and nothing has moved toward it.

## Proposed next steps

Ordered so nothing blocks on the audio assets that don't exist yet.

1. **Decide the two URP questions the migration reopened** — Global Volume + Bloom yes/no;
   VFX Graph package yes/no (recommend: Bloom yes, VFX Graph no).
2. **Amend `Plan-SfxVfx.md` §1.1, §1.2, §6.11 and open question 6** to match the pipeline that
   actually exists, before anyone builds against the Built-in assumptions. The plan is otherwise
   sound and worth keeping.
3. **M4 (trails) first, not M1.** It is the one milestone with no asset dependency, it exercises
   the new queue/depth-write interaction while it is fresh, and it delivers something visible.
   Add `TrailQueue` to `PlaceholderArt`, `TrailTuning` at `Resources/TrailTuning.asset`, the
   `TrailRenderer` in `CreateRockObject`.
4. **M2 (the six events) next** — also asset-free, verifiable with `EventProbe` and
   `Bootstrap.eventDebugLogging`, and it unblocks every later milestone including the non-audio
   polish in §6.
5. **§6.1 aim-arc colour and §6.2 ghost glow** — ~1 hour combined, no audio, highest gamefeel
   payoff per the plan's own ranking.
6. **Answer open question 1, then M1 → M3.** Until clips exist, M1 can only be verified with a
   placeholder beep, and M3 cannot start at all.

## Related

- [`Plan-SfxVfx.md`](Plan-SfxVfx.md) — the plan this audits.
- [`PLAN.md`](PLAN.md) — overall project plan.
