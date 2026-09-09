# System documentation

**Tier 4.** Seven runtime-critical systems — if one of these is wrong, the game does not work.

Each document covers the same four things, in this order:

1. **What it owns** — the single responsibility, stated so overlaps are visible
2. **How it works** — the design, at the depth needed to change it safely
3. **Invariants** — what must stay true. Breaking one of these is how the expensive bugs happened
4. **Traps** — specific things that have already cost someone a day

They do not restate the code. The code in this project carries roughly 30% comment density by
deliberate choice, and those comments explain *why*. These documents give the shape the comments
assume you already have.

| System | Doc | Primary source |
|---|---|---|
| Event system | [`EventSystem.md`](EventSystem.md) | `Assets/Scripts/EventSystems/`, `EventTypes/` |
| Networking | [`Networking.md`](Networking.md) | `Assets/Scripts/Net/`, `relay/` |
| Motion pipeline | [`MotionPipeline.md`](MotionPipeline.md) | `controller/js/`, `Assets/Scripts/Game/Physics/PhoneAttitude.cs` |
| Skip physics | [`SkipPhysics.md`](SkipPhysics.md) | `Assets/Scripts/Game/Physics/` |
| Scoring and turns | [`ScoringAndTurns.md`](ScoringAndTurns.md) | `Assets/Scripts/Managers/` |
| Hosting and certificates | [`HostingAndCertificates.md`](HostingAndCertificates.md) | `Assets/Scripts/Net/`, `deploy/`, `hotspot/` |
| Rock catalog and tuning | [`RockCatalogAndTuning.md`](RockCatalogAndTuning.md) | `Assets/Scripts/Game/Rock*.cs`, `Assets/Rocks/` |

## Why these seven and not more

Scope was chosen at the point where a document still gets maintained. Audio/VFX and
build/distribution were considered and left out: audio is paused on a non-code decision, and
distribution is already covered at the right depth by [`../ZeroSetupPlan.md`](../ZeroSetupPlan.md),
which is live and accurate. Adding a document that duplicates a live one is how two documents
start disagreeing.

If a system doc here starts describing something that is not runtime-critical, that is a signal
it has grown past its purpose.
