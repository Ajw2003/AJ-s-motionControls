# Rock-choosing Part 5 — host settings panel

Implementation plan for the settings panel, approved and built 2026-08-26. See
[`rock-choosing-system.md`](rock-choosing-system.md)'s Build log for the as-built deviations
from this text (JSON-per-host persistence instead of PlayerPrefs, and reachability during both
Lobby and Choosing).

## Context

Parts 1–4 of `docs/plans/rock-choosing-system.md` are built and tested (catalog, tunings,
`RockSelection`, `ShoreRocks` pointing UI, wiring into play, plus an unplanned but tested
mid-match re-pick pause via `TurnManager.EnterChoosing`/`MatchPhase.Choosing`). Part 5 — the
generic settings panel — was deliberately deferred; `RockSelection.RandomRocks` and
`ActiveTuningSet` currently sit as plain `[SerializeField]` fields with an explicit doc-comment
saying Part 5 should read/write them rather than duplicate the state.

Goal: a host-editable settings panel for **Random rocks**, **Tuning set** (Authored/Derived —
the authored-vs-derived comparison switch), and **Rounds per match**, reachable both from the
main lobby before a match starts and from the existing mid-match re-pick pause, with values
persisted to a hand-editable per-host file rather than the Windows registry.

Two decisions the user made explicit:
- **No PlayerPrefs.** The project already has exactly this precedent: `NetworkModeStore`
  ([Scripts/Net/NetworkMode.cs](../../Assets/Scripts/Net/NetworkMode.cs)) reads/writes a plain
  file beside `Application.dataPath`'s parent (repo root in the Editor, next to the exe in a
  build) instead of PlayerPrefs, for the identical reason — "invisible, isn't cleared by
  deleting the game folder, isn't something a player can inspect or reset by hand." Same
  resolution `RelayClient` uses for `relay-url.txt`. This plan reuses that exact pattern, format
  changed to JSON per the user's explicit ask ("save to json and read in unity ... easy to
  change but easy to save and read and edit by hand but not required to").
- **Reachable in Lobby *and* Choosing, not just Lobby.** `TurnManager.IsChoosingPhase` (`Lobby
  || Choosing`) already exists as the single gate `RockSelection`/`ShoreRocks` share for "this
  phase looks like a picking moment." The settings panel reuses the same gate rather than
  inventing a second one, which is exactly "accessible before a match starts, and also at the
  existing mid-session re-pick pause (`reChooseEveryRounds`)" with zero new phase logic.

## Design

### `Scripts/Managers/MatchSettingsStore.cs` (new, static)

Mirrors `NetworkModeStore` exactly, format swapped to JSON via `JsonUtility`:

```csharp
[Serializable]
private class SettingsData
{
    public bool randomRocks;
    public string tuningSet = nameof(TuningSet.Authored); // string, not int — hand-editable
    public int roundsPerMatch = 5;
}
```

- `FileName = "match-settings.json"`, resolved with
  `Path.GetFullPath(Path.Combine(Application.dataPath, "..", FileName))` — identical to
  `NetworkModeStore.ResolvePath`/`RelayClient.ReadOverride`.
- `Load()` — missing file or parse failure both fall back to the struct's defaults (logged as a
  warning on failure, silent on "no file yet," matching `NetworkModeStore.Load`).
- `Save(bool randomRocks, TuningSet tuningSet, int roundsPerMatch)` — `JsonUtility.ToJson(...,
  prettyPrint: true)` so a hand-opened file is readable, wrapped in try/catch logging a warning
  on failure (never throws into gameplay code).
- Add `match-settings.json` to `.gitignore` next to `network-mode.txt` (repo root
  `.gitignore`) — it's a per-host file, not project config.

### `Scripts/Managers/Setting.cs` (new) — the generic list primitives from the original plan text

```csharp
public abstract class Setting
{
    public string Key, Label, Help;
    public abstract string Display { get; }
    public abstract void Next();
    public abstract void Prev();
}
public class ToggleSetting : Setting        // Func<bool> get, Action<bool> set
public class EnumSetting<T> : Setting        // Func<T> get, Action<T> set, cycles Enum.GetValues<T>()
public class IntSetting : Setting            // Func<int> get, Action<int> set, min/max/step
```

Each concrete setting is constructed with a `get`/`set` delegate pair pointing at an **existing**
property elsewhere — no duplicated storage. `set` is wrapped once, centrally, to call
`MatchSettingsStore.Save(...)` after every change (write-through, so leaving the panel open
never loses an edit and there's no separate "apply" step).

### `Scripts/Managers/MatchSettings.cs` (new singleton)

- `Awake()`: `MatchSettingsStore.Load()`, then applies the loaded values directly onto
  `RockSelection.Instance.RandomRocks` / `.ActiveTuningSet` and a new
  `TurnManager.Instance.RoundsPerMatch` setter (below) — **not** duplicate fields. This is why
  `MatchSettings` must be created in `Bootstrap` *after* both `RockSelection.Instance` and
  `TurnManager.Instance` already exist.
- `public List<Setting> All` built once in `Awake`, three entries in the order from the
  original plan text:
  1. `ToggleSetting("randomRocks", "Random rocks", ...)` → `RockSelection.Instance.RandomRocks`
  2. `EnumSetting<TuningSet>("tuningSet", "Tuning set", ...)` → `RockSelection.Instance.ActiveTuningSet`
  3. `IntSetting("roundsPerMatch", "Rounds per match", ..., min:1, max:10, step:1)` →
     `TurnManager.Instance.RoundsPerMatch`
- `public bool CanEdit` — true whenever `Phase != Playing` (Lobby, Choosing, and Results —
  anything that isn't Playing is fine per the original plan's "refused while Phase == Playing"
  rule; Results is included because pressing the start key there routes to `ReturnToLobby()`,
  not straight into a match).

### `TurnManager.cs` change

`RoundsPerMatch` becomes a real property instead of get-only:

```csharp
public int RoundsPerMatch
{
    get => roundsPerMatch;
    set => roundsPerMatch = Mathf.Clamp(value, 1, 10);
}
```

Nothing else in `TurnManager` changes — `NextTurn`/`EndMatch` already read `roundsPerMatch`
through the field, which the setter now also writes.

### `Scripts/UI/MatchSettingsScreen.cs` (new) — OnGUI, following `JoinScreen`/`ResultsScreen`'s exact conventions

- `[SerializeField] KeyCode toggleKey = KeyCode.F4`, scale-based `GUIStyle`s built once in
  `EnsureStyles()` (same `Mathf.Clamp(Screen.height / 1080f, 0.5f, 3f)` scale factor, same
  translucent-panel-texture pattern as the other two screens).
- Toggle only takes effect when `MatchSettings.Instance.CanEdit` is true; if the panel is open
  and the phase changes to Playing (match started while it happened to be up), it force-closes
  itself in `Update()` rather than drawing over active gameplay.
- Renders `MatchSettings.Instance.All` in a `GUILayout` loop — up/down (`KeyCode.UpArrow`/
  `DownArrow`) moves a cursor index, left/right (`KeyCode.LeftArrow`/`RightArrow`) calls
  `Prev()`/`Next()` on the selected setting — so a fourth setting later is one list entry, no
  screen edit, matching the original plan's stated intent.
- Centred modal panel (same `Rect`-and-`GUILayout.BeginArea` shape as `ResultsScreen`), drawn
  over whichever screen is up.
- Exposes `public static bool IsOpen` so `JoinScreen` can suppress its "press SPACE to start"
  line while the modal has focus — avoids two prompts fighting for the same keypress.

### `JoinScreen.cs` / `ResultsScreen.cs` — one line each

- `JoinScreen.DrawJoinDetails`: below the existing start-prompt block, add a compact one-line
  settings summary + `"Press F4 for match settings"` hint (mirrors the existing
  `"{toggleKey} hides this screen"` hint line already there). Suppress the *start* prompt
  specifically while `MatchSettingsScreen` reports open (see above), so pressing Space while the
  modal has cursor focus doesn't also start the match.
- `ResultsScreen.OnGUI`: same one-line hint added next to the existing
  `"Press {StartMatchKey} to play again"` line.

### `Bootstrap.cs` wiring

- New `[SerializeField] bool showMatchSettingsScreen = true;` alongside the other `show*` flags.
- `_ = MatchSettings.Instance;` placed **after** `_ = TurnManager.Instance;` (needs both
  `RockSelection` and `TurnManager` to already exist to apply loaded values — see above) and
  before `CreateSfxDirector()`/screen creation calls, with a comment explaining the ordering
  requirement, matching the existing comment style in this file.
- `CreateMatchSettingsScreen()` following `CreateJoinScreen`/`CreateResultsScreen`'s exact
  `FindFirstObjectByType` + `AddComponent` + `DontDestroyOnLoad` shape, called from `Awake()`
  guarded by `showMatchSettingsScreen`.

## Files touched

| File | Change |
|---|---|
| `Scripts/Managers/MatchSettingsStore.cs` | new — JSON load/save, mirrors `NetworkModeStore` |
| `Scripts/Managers/Setting.cs` | new — `Setting`/`ToggleSetting`/`EnumSetting<T>`/`IntSetting` |
| `Scripts/Managers/MatchSettings.cs` | new singleton — owns the list, applies loaded values, `CanEdit` |
| `Scripts/UI/MatchSettingsScreen.cs` | new — F4 modal, OnGUI |
| `Scripts/Managers/TurnManager.cs` | `RoundsPerMatch` gets a clamped setter |
| `Scripts/UI/JoinScreen.cs` | one hint line + start-prompt suppression while modal open |
| `Scripts/UI/ResultsScreen.cs` | one hint line |
| `Scripts/Managers/Bootstrap.cs` | create `MatchSettings` (ordered after `TurnManager`) + `MatchSettingsScreen` |
| `.gitignore` | add `match-settings.json` next to `network-mode.txt` |
| `docs/plans/rock-choosing-system.md` | Build log entry noting the no-PlayerPrefs / JSON-per-host deviation from the original Part 5 text |

`RockSelection.cs`'s existing `RandomRocks`/`ActiveTuningSet` properties and doc-remark are
reused as-is — no change needed there beyond what `MatchSettings.Awake` calls into them.

## Verification

No out-of-band compile check is possible (Unity holds the project lock) — verify by alt-tabbing
to the open Editor and checking the Console for errors after each file lands, per this repo's
established workflow.

In-editor, once compiling clean:
1. Play from Bootstrap. In the Lobby, confirm the one-line settings summary + "Press F4" hint
   appears under the join panel; press F4, confirm the modal opens, arrow through all three
   settings, confirm values change and `match-settings.json` is written/updated at the repo root
   (`cat match-settings.json` from a shell) with each change.
2. Toggle Random rocks on, start a match, confirm every player gets an assigned rock (existing
   `AssignRandomRocks` behavior) and F4 mid-match (Playing) does nothing / panel doesn't open.
3. Trigger a mid-match re-pick (`forceChooseKey`, default `C`) to enter `Choosing`; confirm F4
   opens the panel there too and edits persist the same way.
4. Restart the Editor session entirely; confirm settings reload from `match-settings.json`
   rather than resetting to code defaults — proving the persistence round-trips.
5. Hand-edit `match-settings.json` (e.g. change `roundsPerMatch` to `3`) while the game is not
   running, relaunch, confirm the Results screen's "after N rounds" text reflects the hand-edited
   value — proving it's genuinely hand-editable, not just app-written.
