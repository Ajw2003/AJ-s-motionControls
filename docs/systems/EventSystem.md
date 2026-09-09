# Event system

**Source:** `Assets/Scripts/EventSystems/EventManager.cs`, `Assets/Scripts/EventTypes/`,
`Assets/Scripts/Utils/Singleton.cs`

## What it owns

Decoupled communication between systems that must not know about each other. `RockController`
announces a skip; it has no idea `ScoreManager` and `SfxDirector` are both listening, and it does
not learn if one of them goes away.

It owns **delivery**. It does not own ordering policy, and it does not own what any event means.

## How it works

Publish/subscribe keyed on the event's type. Subscribers are held in a `WeakReference`, so a
listener that is garbage or destroyed is dropped on the next publish even if it never
unsubscribed — a safety net, not a licence to skip unsubscribing.

```csharp
EventManager.Instance?.Subscribe<SkipOccurredEvent>(this, e => Handle(e.SkipNumber));
...
void OnDestroy() => EventManager.Instance?.UnsubscribeFromAllEvents(this);
```

Events are plain classes implementing the marker interface `IEvent`, living under
`Scripts/EventTypes/<Category>Events/` and namespaced `EventTypes.<Category>Events`. They are
immutable: get-only properties, set once in the constructor. Copy an existing file in that folder
rather than writing one from scratch — the shape is consistent on purpose.

Three categories exist today: `MatchEvents` (roster, turns, phase, score, standings),
`SkipEvents` (launched, skipped, sunk, throw completed) and `AudioEvents`.

### It was ported, and the port is the interesting part

`EventManager` and `SingletonBase` came from another project (FactoryTM). Four changes were
required before the port worked here, all of them forced by this project's four-scene structure
(`EventManager.cs:13-22`):

1. `persistBetweenScenes` is true, so the manager survives a scene load
2. Destroyed-subscriber detection accounts for Unity's fake-null semantics
3. `OnDestroy` calls `base.OnDestroy()`, so the singleton static is actually cleared
4. `UnsubscribeFromAllEvents` dropped reflection and now removes *every* matching subscription
   rather than only the first

Change 2 is the one worth understanding. `WeakReference.Target` is typed `object`, so a plain
`== null` is reference equality and **bypasses Unity's overloaded `==`**, which is what reports a
destroyed `UnityEngine.Object` as null. Without the explicit `UnityEngine.Object` branch,
destroyed MonoBehaviours keep getting invoked and throw `MissingReferenceException` on every
publish — which, with multiple scenes, means on every transition.

## Invariants

- **Every scene-local subscriber unsubscribes in `OnDestroy`.** Mandatory, not advisory: each
  scene transition destroys every subscriber in the outgoing scene.
- **Publish and subscribe through `EventManager.Instance?.`** — always the null-conditional. This
  is a documented deviation from the usual "don't defend against impossible nulls" rule:
  `SingletonBase.Instance`'s getter already uses Unity's overloaded `==` internally, so the
  fake-null window is narrow, but the `_isQuitting` teardown path can still hand back a destroyed
  instance.
- **Events are immutable.** A subscriber must not be able to change what a later subscriber sees.
- **Delivery is in subscription order,** and some code depends on that. See below.

## Traps

**Subscription order is load-bearing, and it is set by `Bootstrap` creation order.** Events are
delivered in the order subscribers registered. `ScoreManager` and `TurnManager` both handle
`ThrowCompletedEvent`; `ScoreManager` must be created first so the final throw's points are
banked before `TurnManager` reads the standings to end the match. Created the other way round,
the winning throw is missing from the results screen. `Bootstrap.cs:69-73` says this in place —
do not reorder that file without reading the comments, which exist for exactly this reason.

**Publish failures are swallowed by default.** Set `EventManager.DebugLoggingEnabled` (exposed on
`Bootstrap`) while wiring new events, or a handler that throws will look like an event that never
fired.

**The weak reference is a safety net, not the design.** Relying on it means subscriptions live
until the next publish happens to prune them, and `GetSubscriptionCount<T>()` includes records not
yet pruned — which is why that method is documented as diagnostics-only.

## Verification

`RockSkipping/Verify/Run Event System Verification` proves publish/subscribe survives a scene load
and that destroyed subscribers are dropped. It writes `verification-result.txt` at the repo root.
It enters Play mode, so the headless invocation deliberately has no `-quit` — see the root
[`README.md`](../../README.md).
