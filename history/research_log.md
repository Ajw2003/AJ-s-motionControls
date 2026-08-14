# Rock Skipping Limit Research Log

## 1. Initial Grep Searches for Hardcoded Limits
**Action:** Searched the codebase for hard limits, looking for the number `22`, variables like `MaxSkips`, or hardcoded loops.
- Checked `c:\Users\aj\Desktop\GameDev\RockSkipping\Assets\Scripts` for `22`.
- **Finding:** Found `public float PredictSeconds = 22f;` in `Assets/Scripts/Game/SkipTuning.cs` (Line 117). However, further inspection showed this only controls the aim preview's line renderer horizon, and the value is actually overridden to `60` in the deployed `SkipTuning.asset`.
- **Finding:** Searched for `Max.*Skip` and `limit`. No hardcoded loop constraints or integer max skip limits were found in any physics loops.

## 2. Investigation of Physics Simulation
**Action:** Examined `Assets/Scripts/Game/SkipPhysics.cs`, which governs the mid-flight calculations.
- **Line 171:** At the start of a bounce calculation, the rock's incoming speed is checked:
  ```csharp
  if (speed < tuning.MinSkipSpeed || incidenceDeg > MaxIncidenceDeg) { next.Sunk = true; ... }
  ```
- **Line 203:** Speed is bled off geometrically on each bounce based on throwing power:
  ```csharp
  var speedKept = Mathf.Lerp(tuning.SpeedKeptSoft, tuning.SpeedKeptHard, next.Power);
  velocity.x *= speedKept;
  velocity.z *= speedKept;
  ```

## 3. Review of the Tuning Asset
**Action:** Viewed `Assets/Resources/SkipTuning.asset` to see the actual deployed float values driving the physics formula.
- **Line 18:** `MaxSpeed: 40` (The absolute fastest a rock can be thrown)
- **Line 23:** `SpeedKeptHard: 0.9` (At max power, the rock retains 90% of its speed per bounce)
- **Line 24:** `MinSkipSpeed: 4` (The minimum speed required to bounce without sinking)

## 4. Mathematical Conclusion
The max limit of 22 skips is an **emergent mathematical property** of the geometric decay of the rock's speed, not a programmed integer limit. 

If a player throws a rock at the absolute maximum speed (`40 m/s`) with maximum power (`SpeedKeptHard = 0.9`), the speed decays by 10% every single bounce. 
The number of skips (n) can be found by solving:  
`MaxSpeed * (SpeedKeptHard ^ n) < MinSkipSpeed`
`40 * (0.9 ^ n) < 4`

`40 * (0.9)^21 = 4.37` m/s (Successfully skips)
`40 * (0.9)^22 = 3.93` m/s (Fails to skip)

Because `3.93 < 4`, the `speed < tuning.MinSkipSpeed` check on Line 171 triggers exactly on the 22nd bounce attempt, sinking the rock. The game caps at 22 skips because it is mathematically impossible to sustain a speed > 4 m/s for more than 21 bounces given the current `SkipTuning.asset` parameters.
