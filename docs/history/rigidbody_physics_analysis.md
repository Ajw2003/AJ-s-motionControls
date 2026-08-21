# Theoretical Migration to Unity Rigidbody Physics

This document outlines a theoretical plan and analysis for replacing the existing custom `SkipPhysics` engine with Unity's built-in Rigidbody system.

## Background Context
Currently, `SkipPhysics.cs` drives the rock using a custom, pure-math deterministic simulation. It calculates parabolic arcs, bounce lift, and velocity decay frame-by-frame based on highly tuned inputs like `Power`, `FaceAngleDeg`, and `DynamicSpeedRetention`. The simulation is entirely separated from Unity's PhysX engine.

## The Question: Is it Possible?
**Yes, it is entirely possible.** The migration would involve replacing the manual position updates in `RockController` with a `Rigidbody` component, converting the water plane into a trigger/collision volume, and using `OnCollisionEnter` or `OnTriggerEnter` to calculate and apply bounce forces. 

However, it represents a fundamental shift in how the game feels and is architected.

## Benefits of "Actual" Physics

Using Unity's built-in Rigidbody system brings several distinct advantages:

1. **Emergent Gameplay**: Rocks could interact organically with the environment. If you wanted to add obstacles (logs, buoys, lily pads) or irregular shorelines, standard Unity colliders would handle the interactions naturally without needing custom math.
2. **Environmental Forces**: Wind, water currents, or ripples could easily apply continuous forces (`AddForce`) to the Rigidbody, allowing for more dynamic weather or water states.
3. **Built-in Tooling**: You can leverage Unity's Physics Materials (bounciness, friction), mass, and drag settings natively rather than writing custom equivalents.
4. **Simplification of the Main Loop**: `RockController` would no longer need to manage its own fixed-step accumulator and loop; Unity's `FixedUpdate` handles integration automatically.

## Hurdles and Downsides

Moving to Rigidbody physics introduces significant architectural and game-feel challenges:

> [!WARNING]
> **The Aim Preview Problem**
> Currently, `SkipPhysics.Predict()` runs hundreds of iterations of the pure `Step()` function in a fraction of a millisecond to draw the predicted arc. 
> 
> With Rigidbody physics, you cannot simply fast-forward the math. To draw a preview arc, you would have to create a separate, invisible `PhysicsScene`, instantiate a ghost rock and water plane, and call `physicsScene.Simulate(Time.fixedDeltaTime)` in a loop, recording the ghost rock's position at every step. This is **substantially more computationally expensive** and complex to manage than the current pure-math approach.

> [!CAUTION]
> **Loss of the "Tuned" Arcade Feel**
> The current system has variables carefully tuned for skipping, such as `FaceAngleDeg` grading, `MinExitRiseSpeed`, and `DynamicSpeedRetention`. Unity's physics engine expects real-world Newtonian reactions. 
> 
> Real rocks skip because of complex fluid dynamics and surface tension, which Unity's rigidbodies do not simulate. To make the Rigidbody skip in a satisfying way, you would have to intercept the collision anyway, calculate a fake "lift" force, and manually overwrite the rigidbody's velocity—effectively fighting the physics engine to recreate the custom math you just deleted.

> [!IMPORTANT]
> **Network Determinism**
> If you plan to add multiplayer, the current custom physics engine is 100% deterministic—the same inputs yield the exact same skips on every device. Unity's PhysX engine is notoriously non-deterministic across different devices and framerates. Syncing a physics-based skip across a network is vastly harder than sending the initial `SkipState` variables.

## Summary 

While moving to Rigidbody physics opens the door for organic interactions with complex environments, it would likely **break the aim preview**, introduce **performance overhead** for predictions, and require you to **fight the physics engine** to maintain the carefully tuned arcade feel of a skipping stone.

The current `SkipPhysics` system is highly optimized for its specific, isolated task. Replacing it is a tradeoff of giving up tight control and cheap prediction in exchange for environmental interactivity.
