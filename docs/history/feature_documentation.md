# Feature Documentation: Dynamic Speed Retention Based on Throw Mechanics

> **Status: Implemented.** This document is written as a forward-looking proposal, but the
> feature is live — `DynamicSpeedRetention` is calculated at launch and applied per bounce in
> `Assets/Scripts/Game/SkipPhysics.cs` (field at :44, applied at :208, replacing
> `SpeedKeptSoft`/`SpeedKeptHard`). Read the future tense below as a description of what was
> built. It supersedes [research_log.md](research_log.md).

## 1. Layman Summary
To resolve the rigid 22-skip limit and make the game more skill-based, the skip physics will now rely on a single, dynamically calculated "Speed Retention" value. Instead of a fixed 90% speed kept on every skip, this value will be determined by how well the player throws the rock. Throwing at the perfect angle and with maximum power will yield a much higher speed retention (allowing the rock to go further and skip more times), whereas poor angles or weak throws will result in lower retention, causing the rock to fall short and sink faster.

## 2. Technical Specifications
The skip limit is derived from:
- `MaxSpeed` = 40 (maximum initial power)
- `MinSkipSpeed` = 4 (speed at which the rock sinks)
- `DynamicSpeedRetention` = A single variable replacing previous hard/soft retention values, calculated per throw.

### Dynamic Speed Retention Calculation
The retention value is calculated when the rock is thrown, using:
1. **Throw Power:** The initial velocity or force applied by the player.
2. **Throw Angle:** The angle at which the rock hits the water.
3. **Optimal Angle:** The theoretically perfect angle for skipping (e.g., 10-15 degrees).

**Formula Concept:**
- `AngleFactor = 1.0 - Clamp(Abs(ThrowAngle - OptimalAngle) / MaxAngleTolerance, 0.0, 1.0)`
- `PowerFactor = ThrowPower / MaxSpeed`
- `DynamicSpeedRetention = MinRetention + (MaxRetention - MinRetention) * (AngleFactor * 0.6 + PowerFactor * 0.4)`

*Example Target Values:*
- `MinRetention` = 0.75 (Sinks very quickly)
- `MaxRetention` = 0.99 (Allows a massive number of skips for perfect throws)
- `OptimalAngle` = 15 degrees

## 3. Process/Feature Definitions
### Implementation Details
The game will now use a single `DynamicSpeedRetention` property for the rock's lifetime, calculated once at launch. 

**Dynamic Calculation Logic:**
- At the moment of release, the game calculates the initial throw power and angle.
- It calculates the single `DynamicSpeedRetention` value.
- On every skip, `CurrentSpeed = CurrentSpeed * DynamicSpeedRetention`.
- Once `CurrentSpeed < MinSkipSpeed`, the rock sinks.

This entirely removes the need for arbitrary limits or multiple hardcoded variables, consolidating the physics into one skill-based outcome.

## 4. Next Steps for Implementation Agent
1. Locate the core physics script for the rock.
2. Remove any fixed retention variables.
3. Introduce inspector variables for `OptimalAngle`, `MinRetention`, `MaxRetention`, and `DynamicSpeedRetention`.
4. Implement the logic to calculate `DynamicSpeedRetention` based on the rock's initial throwing power and trajectory angle upon release.
5. Apply this single retention value to the rock's speed degradation upon each skip.
6. Compile the code, and test in the engine to ensure perfect throws feel rewarding and poor throws sink rapidly.
7. Stop for User verification upon completing the code changes.
