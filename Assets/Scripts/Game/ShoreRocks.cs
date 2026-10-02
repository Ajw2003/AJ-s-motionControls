using System.Collections.Generic;
using EventSystems;
using EventTypes.MatchEvents;
using Managers;
using Net;
using UnityEngine;

namespace Game
{
    /// <summary>
    /// The six stones on the shore in the lobby, and the per-player pointer that picks
    /// one by heading alone. See docs/plans/rock-choosing-system.md Part 3.
    /// </summary>
    /// <remarks>
    /// Active while <see cref="TurnManager.IsChoosingPhase"/> is true — Lobby (a fresh
    /// match) or Choosing (a mid-match re-pick TurnManager can trigger periodically or on
    /// demand). This is the other half of the fix the plan's constraints call for:
    /// AimPreview only reacts to AIM while Playing, this only reacts during a choosing
    /// phase, so the two never fight over the same frame. Built by AimPreview alongside
    /// the placeholder water, sharing its throw origin and camera framing — see
    /// <see cref="Bind"/>.
    ///
    /// Yaw only, on purpose. A player thinking about their choice naturally lets the phone
    /// drop, and a 2-D pick would lose their selection when they did. Absolute compass
    /// heading is deliberately not used anywhere in this project (it disagrees across
    /// platforms), so the fan is centred on wherever a player happened to be pointing when
    /// they tapped to calibrate — not on the TV. See the plan's "honest limitation" note.
    ///
    /// A PICK_ROCK carries no rock id (see PickRockMessage) — it means "confirm whichever
    /// stone this player is currently pointing at", which only this class can answer. It
    /// is the one thing in the rock-choosing feature that resolves that event into an
    /// actual RockSelection.Pick call.
    /// </remarks>
    public class ShoreRocks : MonoBehaviour
    {
        private RockCatalog _catalog;
        private RockSelectTuning _tuning;
        private RelayClient _client;

        private Transform _origin;
        private Stone[] _stones = System.Array.Empty<Stone>();
        private readonly Dictionary<string, Pointer> _pointers = new();

        private GUIStyle _nameStyle;
        private GUIStyle _blurbStyle;
        private GUIStyle _tickStyle;
        private Camera _camera;

        private class Stone
        {
            public RockDefinition Definition;
            public Transform Transform;
            public Renderer Renderer;
            public Material Material;
            public Color BaseColour;
            public Vector3 BaseScale;
            public float CenterYawDeg;
        }

        private class Pointer
        {
            public float TargetYawDeg;
            public float SmoothedYawDeg;
            public float LastAimTime;
            public int HighlightIndex = -1;
            public bool Confirmed;
            public int ConfirmedIndex = -1;
            public Transform Marker;
        }

        private void Awake()
        {
            _catalog = RockCatalog.LoadOrWarn();

            _tuning = Resources.Load<RockSelectTuning>("RockSelectTuning");
            if (_tuning == null)
            {
                _tuning = RockSelectTuning.CreateDefault();
                Debug.LogWarning("[ShoreRocks] No RockSelectTuning at Resources/RockSelectTuning — " +
                                 "shore layout and pointer feel are on code defaults, which cannot " +
                                 "be tuned without a recompile.");
            }

            EventManager.Instance?.Subscribe<PlayerLeftEvent>(this, OnPlayerLeft);
            EventManager.Instance?.Subscribe<MatchPhaseChangedEvent>(this, OnMatchPhaseChanged);
            EventManager.Instance?.Subscribe<PlayerColourChangedEvent>(this, OnPlayerColourChanged);

            // Awake, not Start: Bind() can SetActive(false) before the first Start would run,
            // which would defer the subscription to the first re-show. Awake runs exactly once
            // regardless, and AimPreview (our creator) is built after RelayClient.
            BindRelay();
        }

        /// <summary>
        /// Lays the catalog out on the shore around <paramref name="origin"/>. Called by
        /// AimPreview right after the throw origin exists — see WaterRipples.Bind for the
        /// same AddComponent-then-Bind shape and why it exists.
        /// </summary>
        public void Bind(Transform origin)
        {
            _origin = origin;
            BuildStones();

            // No MatchPhaseChangedEvent has fired yet at boot — TurnManager starts in
            // Lobby and only announces a CHANGE — so the initial state has to be read
            // directly rather than waited for.
            var choosing = !TurnManager.HasInstance || TurnManager.IsChoosingPhase(TurnManager.Instance.Phase);
            SetVisible(choosing);
        }

        private void OnDestroy()
        {
            EventManager.Instance?.UnsubscribeFromAllEvents(this);

            if (_client != null)
            {
                _client.AimReceived -= OnAim;
                _client.PickRockReceived -= OnPickRock;
            }

            foreach (var stone in _stones)
            {
                if (stone.Material != null) Destroy(stone.Material);
            }
        }

        private void BuildStones()
        {
            var count = _catalog.Count;
            _stones = new Stone[count];

            for (var i = 0; i < count; i++)
            {
                var rock = _catalog.At(i);
                var yaw = CenterYawDeg(i, count);

                var go = new GameObject($"Shore_{rock.Id}");
                go.transform.SetParent(transform, false);

                var filter = go.AddComponent<MeshFilter>();
                filter.sharedMesh = rock.Mesh;

                var renderer = go.AddComponent<MeshRenderer>();
                var material = PlaceholderArt.Create(rock.Tint, PlaceholderArt.StoneQueue);
                renderer.material = material;

                go.transform.localPosition = LocalPositionAt(yaw);
                go.transform.localRotation = Quaternion.Euler(rock.MeshRotation);
                go.transform.localScale = rock.LocalScale;

                _stones[i] = new Stone
                {
                    Definition = rock,
                    Renderer = renderer,
                    Transform = go.transform,
                    Material = material,
                    BaseColour = rock.Tint,
                    BaseScale = rock.LocalScale,
                    CenterYawDeg = yaw,
                };
            }
        }

        /// <summary>Evenly spread across the fan; a single rock sits dead centre.</summary>
        private float CenterYawDeg(int index, int count) =>
            count <= 1 ? 0f : Mathf.Lerp(-_tuning.FanDegrees, _tuning.FanDegrees, index / (float)(count - 1));

        private Vector3 LocalPositionAt(float yawDeg, float radiusScale = 1f) =>
            Quaternion.Euler(0f, yawDeg, 0f) * Vector3.forward * (_tuning.ShoreRadius * radiusScale)
            + Vector3.up * _tuning.ShoreHeight;

        private void Update()
        {
            // No inLobby re-check here on purpose: visibility is driven entirely by
            // OnMatchPhaseChanged below, not polled. SetActive(false) stops Unity from
            // calling Update on this GameObject at all — a poll here would work right up
            // until the first time it hid itself, and then never see the phase come back
            // to Lobby to reactivate.
            if (_origin == null) return;

            PruneStalePointers();

            foreach (var pointer in _pointers.Values)
            {
                pointer.SmoothedYawDeg = Mathf.LerpAngle(
                    pointer.SmoothedYawDeg, pointer.TargetYawDeg,
                    1f - Mathf.Exp(-_tuning.PointerSmoothing * Time.deltaTime));

                pointer.HighlightIndex = ResolveHighlight(pointer.SmoothedYawDeg, pointer.HighlightIndex);

                if (pointer.Marker != null)
                {
                    pointer.Marker.localPosition =
                        LocalPositionAt(pointer.SmoothedYawDeg, 0.75f) + Vector3.up * 0.6f;
                }
            }

            UpdateStoneLooks();
        }

        private void BindRelay()
        {
            if (!RelayClient.HasInstance)
            {
                Debug.LogWarning("[ShoreRocks] No RelayClient at Awake; the shore will not react to phone aim or picks.");
                return;
            }

            _client = RelayClient.Instance;
            _client.AimReceived += OnAim;
            _client.PickRockReceived += OnPickRock;
        }

        private void SetVisible(bool isVisible)
        {
            if (gameObject.activeSelf != isVisible) gameObject.SetActive(isVisible);
        }

        private void PruneStalePointers()
        {
            List<string> stale = null;
            foreach (var pair in _pointers)
            {
                if (Time.time - pair.Value.LastAimTime <= _tuning.PointerStaleSeconds) continue;
                (stale ??= new List<string>()).Add(pair.Key);
            }

            if (stale == null) return;
            foreach (var playerId in stale) RemovePointer(playerId);
        }

        private void RemovePointer(string playerId)
        {
            if (!_pointers.TryGetValue(playerId, out var pointer)) return;
            if (pointer.Marker != null) Destroy(pointer.Marker.gameObject);
            _pointers.Remove(playerId);
        }

        // ------------------------------------------------------------------ inbound

        private void OnAim(AimMessage aim)
        {
            if (!TurnManager.HasInstance || !TurnManager.IsChoosingPhase(TurnManager.Instance.Phase)) return;
            if (string.IsNullOrEmpty(aim.playerId)) return;

            var attitude = PhoneAttitude.ToUnity(aim.qw, aim.qx, aim.qy, aim.qz);
            var yaw = PhoneAttitude.HeadingDeg(attitude);

            if (!_pointers.TryGetValue(aim.playerId, out var pointer))
            {
                pointer = new Pointer { TargetYawDeg = yaw, SmoothedYawDeg = yaw };
                pointer.Marker = CreateMarker(aim.playerId);
                _pointers[aim.playerId] = pointer;
            }

            pointer.TargetYawDeg = yaw;
            pointer.LastAimTime = Time.time;
        }

        private void OnPickRock(PickRockMessage message)
        {
            if (!TurnManager.HasInstance || !TurnManager.IsChoosingPhase(TurnManager.Instance.Phase)) return;
            if (string.IsNullOrEmpty(message.playerId) || _stones.Length == 0) return;

            // No pointer yet means this phone never sent an AIM frame — the documented
            // no-sensor-client limitation. Confirming still has to mean something, so it
            // falls back to the shore's first stone rather than doing nothing.
            var index = _pointers.TryGetValue(message.playerId, out var pointer) && pointer.HighlightIndex >= 0
                ? pointer.HighlightIndex
                : 0;

            var rock = _catalog.At(index);
            if (rock == null) return;

            RockSelection.Instance.Pick(message.playerId, rock.Id);

            if (pointer == null)
            {
                pointer = new Pointer { HighlightIndex = index, TargetYawDeg = _stones[index].CenterYawDeg, SmoothedYawDeg = _stones[index].CenterYawDeg };
                pointer.Marker = CreateMarker(message.playerId);
                _pointers[message.playerId] = pointer;
            }

            pointer.Confirmed = true;
            pointer.ConfirmedIndex = index;
        }

        private void OnPlayerLeft(PlayerLeftEvent e) => RemovePointer(e.PlayerId);

        /// <summary>
        /// Re-tints a dot already on screen, so a colour picked mid-lobby lands immediately
        /// rather than at the next phase change. A player with no marker yet needs nothing —
        /// CreateMarker reads the colour when it builds one.
        /// </summary>
        private void OnPlayerColourChanged(PlayerColourChangedEvent e)
        {
            if (!_pointers.TryGetValue(e.PlayerId, out var pointer) || pointer.Marker == null) return;

            var renderer = pointer.Marker.GetComponent<MeshRenderer>();
            if (renderer != null && renderer.material != null) renderer.material.color = e.Colour;
        }

        /// <summary>
        /// The colour this player owns, or the shore's own highlight colour when the colour
        /// manager is not running — the same "degrade to the old look" tolerance the rocks
        /// map in BuildSpectate has.
        /// </summary>
        private Color ColourFor(string playerId) =>
            PlayerColours.HasInstance ? PlayerColours.Instance.For(playerId) : _tuning.HighlightColour;

        private void OnMatchPhaseChanged(MatchPhaseChangedEvent e)
        {
            var enteringChoosing = TurnManager.IsChoosingPhase(e.To);
            SetVisible(enteringChoosing);

            if (!enteringChoosing) return;

            // Every fresh Lobby AND every mid-match Choosing pause re-picks from scratch,
            // same timing as RockSelection's own clear — see its OnMatchPhaseChanged.
            foreach (var pointer in _pointers.Values)
            {
                if (pointer.Marker != null) Destroy(pointer.Marker.gameObject);
            }
            _pointers.Clear();
        }

        // ------------------------------------------------------------------ highlight

        private int ResolveHighlight(float yawDeg, int previousIndex)
        {
            if (_stones.Length == 0) return -1;

            var nearest = 0;
            var bestDistance = float.MaxValue;
            for (var i = 0; i < _stones.Length; i++)
            {
                var distance = Mathf.Abs(Mathf.DeltaAngle(yawDeg, _stones[i].CenterYawDeg));
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    nearest = i;
                }
            }

            if (previousIndex < 0 || previousIndex >= _stones.Length || previousIndex == nearest)
                return nearest;

            // Sticky: only give up the current stone once the alternative is closer by
            // more than the margin, so resting near a boundary does not flicker.
            var distanceToPrevious = Mathf.Abs(Mathf.DeltaAngle(yawDeg, _stones[previousIndex].CenterYawDeg));
            return distanceToPrevious - bestDistance > _tuning.StickyMarginDeg ? nearest : previousIndex;
        }

        private void UpdateStoneLooks()
        {
            for (var i = 0; i < _stones.Length; i++)
            {
                var stone = _stones[i];
                var index = i;

                // Whose stone this is, if anyone's — kept rather than a bool so the stone
                // can be tinted toward that player's own colour. This is the first thing
                // outside the pointer dots to read a player colour, and the hook the rest
                // of the rock-colouring work is meant to build on.
                // Plain loop, not LINQ: this runs per stone, every lobby frame (#63).
                string owner = null;
                var highlightedHere = false;
                foreach (var pair in _pointers)
                {
                    var pointer = pair.Value;
                    if (pointer.Confirmed && pointer.ConfirmedIndex == index) owner = pair.Key;
                    else if (pointer.HighlightIndex == index) highlightedHere = true;
                }
                var confirmedHere = owner != null;
                highlightedHere &= !confirmedHere;

                var colour = confirmedHere ? ConfirmedColourFor(owner, stone.BaseColour)
                    : highlightedHere ? _tuning.HighlightColour
                    : stone.BaseColour;
                var scale = stone.BaseScale * (confirmedHere || highlightedHere ? _tuning.HighlightScale : 1f);

                // Written only on change: both setters cross into native code.
                if (stone.Material.color != colour) stone.Material.color = colour;
                if (stone.Transform.localScale != scale) stone.Transform.localScale = scale;
            }
        }

        // ------------------------------------------------------------------ pointer markers

        /// <summary>
        /// A player's dot, in the colour that player owns.
        /// </summary>
        /// <remarks>
        /// This used to take an ordinal — <c>_pointers.Count</c> at the call site — and mix
        /// it into a hash of the player id to index a palette held here. Every phase change
        /// clears <c>_pointers</c> (see OnMatchPhaseChanged), so that ordinal was really
        /// "how many other people had aimed before you, this round": a player's colour moved
        /// between rounds, and hash-plus-ordinal collided freely inside eight slots. Colour
        /// is now owned by Managers.PlayerColours, keyed by identity alone, and the palette
        /// itself lives on the PlayerColourPalette asset where it can be edited.
        /// </remarks>
        /// <summary>
        /// A claimed stone, in its owner's colour. The blend itself lives on
        /// PlayerColours.StoneColourFor, shared with the thrown stone and the aim ghost so
        /// the stone a player picks looks like the stone they throw.
        /// </summary>
        private Color ConfirmedColourFor(string playerId, Color baseColour) =>
            PlayerColours.HasInstance
                ? PlayerColours.Instance.StoneColourFor(playerId, baseColour)
                : _tuning.ConfirmedColour;

        private Transform CreateMarker(string playerId)
        {
            var marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            marker.name = $"Pointer_{playerId}";
            Destroy(marker.GetComponent<Collider>());

            var material = PlaceholderArt.Create(ColourFor(playerId), PlaceholderArt.StoneQueue);
            marker.GetComponent<MeshRenderer>().material = material;

            marker.transform.SetParent(transform, false);
            marker.transform.localScale = Vector3.one * 0.35f;
            return marker.transform;
        }

        // ------------------------------------------------------------------ labels

        private void OnGUI()
        {
            if (!gameObject.activeSelf) return;

            // Resolved lazily rather than only in Awake: Camera.main can still be
            // null when this component wakes, and a one-shot lookup would leave the
            // labels permanently blank. Mirrors RockLauncher's retry.
            if (_camera == null) _camera = Camera.main;
            if (_camera == null) return;

            EnsureStyles();

            for (var i = 0; i < _stones.Length; i++)
            {
                DrawStoneLabel(i);
            }

            DrawConfirmations();
        }

        /// <summary>
        /// The rock's name over its description, as one block whose bottom sits just above
        /// the rock's actual top, and whose description wraps to the gap between this rock and
        /// its nearest neighbour on screen.
        /// </summary>
        /// <remarks>
        /// Anchored to renderer bounds rather than a fixed height above the centre: the old
        /// +0.8 m / +0.5 m offsets were set for 0.75-scale stones, and at x2.47 they put the
        /// text inside the rock. Wrapping to the neighbour gap is what lets the description be
        /// full size without running into the next one, and without the old fixed 160 px box
        /// that cut most descriptions off mid-sentence (issue #41).
        /// </remarks>
        private void DrawStoneLabel(int index)
        {
            var stone = _stones[index];
            if (stone?.Renderer == null) return;

            var bounds = stone.Renderer.bounds;
            var anchor = _camera.WorldToScreenPoint(new Vector3(bounds.center.x, bounds.max.y + 0.25f, bounds.center.z));
            if (anchor.z <= 0f) return;

            var width = LabelWidthFor(index, anchor.x);
            var x = anchor.x - width * 0.5f;
            var bottom = Screen.height - anchor.y;

            var blurb = stone.Definition.Blurb;
            var blurbHeight = string.IsNullOrEmpty(blurb) ? 0f : _blurbStyle.CalcHeight(new GUIContent(blurb), width);
            var nameHeight = _nameStyle.CalcHeight(new GUIContent(stone.Definition.DisplayName), width);

            var top = bottom - blurbHeight - nameHeight;
            DrawShadowed(new Rect(x, top, width, nameHeight), stone.Definition.DisplayName, _nameStyle);
            if (blurbHeight > 0f) DrawShadowed(new Rect(x, top + nameHeight, width, blurbHeight), blurb, _blurbStyle);
        }

        /// <summary>
        /// Screen width a label may take: the distance to the nearer neighbouring rock, so
        /// adjacent descriptions never overlap, capped so a lone rock's text stays a column.
        /// </summary>
        private float LabelWidthFor(int index, float screenX)
        {
            var cap = _blurbStyle.fontSize * 16f;
            var nearest = cap;

            foreach (var neighbour in new[] { index - 1, index + 1 })
            {
                if (neighbour < 0 || neighbour >= _stones.Length || _stones[neighbour]?.Renderer == null) continue;

                var other = _camera.WorldToScreenPoint(_stones[neighbour].Renderer.bounds.center);
                if (other.z <= 0f) continue;
                nearest = Mathf.Min(nearest, Mathf.Abs(other.x - screenX) * 0.92f);
            }

            return Mathf.Max(nearest, _blurbStyle.fontSize * 6f);
        }

        /// <summary>
        /// Who has confirmed each rock, stacked under it: a tick and their display name in
        /// their own colour. Stacked because two players may confirm the same rock - they
        /// used to be drawn on the same spot - and the name, not the raw device id the old
        /// label printed.
        /// </summary>
        private void DrawConfirmations()
        {
            var stacked = new int[_stones.Length];

            foreach (var pair in _pointers)
            {
                if (!pair.Value.Confirmed) continue;

                var index = pair.Value.ConfirmedIndex;
                if (index < 0 || index >= _stones.Length || _stones[index]?.Renderer == null) continue;

                var bounds = _stones[index].Renderer.bounds;
                var anchor = _camera.WorldToScreenPoint(new Vector3(bounds.center.x, bounds.min.y - 0.15f, bounds.center.z));
                if (anchor.z <= 0f) continue;

                var lineHeight = _tickStyle.fontSize * 1.4f;
                var width = LabelWidthFor(index, anchor.x);
                var rect = new Rect(anchor.x - width * 0.5f, Screen.height - anchor.y + lineHeight * stacked[index],
                                    width, lineHeight);
                stacked[index]++;

                var name = PlayerNames.HasInstance ? PlayerNames.Instance.DisplayNameFor(pair.Key) : pair.Key;
                var previous = GUI.color;
                GUI.color = PlayerColours.HasInstance ? PlayerColours.Instance.For(pair.Key) : Color.white;
                DrawShadowed(rect, "\u2713 " + name, _tickStyle);
                GUI.color = previous;
            }
        }

        /// <summary>
        /// Text with a dark copy under it, so it reads against bright sky and pale water
        /// alike. The shadow ignores GUI.color so a player-coloured label keeps a dark edge.
        /// </summary>
        private static void DrawShadowed(Rect rect, string text, GUIStyle style)
        {
            var offset = Mathf.Max(1f, style.fontSize * 0.08f);
            var previous = GUI.color;

            GUI.color = new Color(0f, 0f, 0f, 0.75f * previous.a);
            GUI.Label(new Rect(rect.x + offset, rect.y + offset, rect.width, rect.height), text, style);

            GUI.color = previous;
            GUI.Label(rect, text, style);
        }

        private void EnsureStyles()
        {
            if (_nameStyle != null) return;

            // Scaled off screen height like every other TV screen, so the names read from a
            // couch on a TV and not only in a small editor Game view. They were fixed at 16 and
            // 12 px before - about the size of a phone's fine print on a 1080p TV.
            var scale = Mathf.Clamp(Screen.height / 1080f, 0.5f, 3f);

            // White text everywhere: GUI.color multiplies into it, which is how the shadow and
            // the player-coloured confirmation ticks get their colours from DrawShadowed.
            _nameStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.LowerCenter,
                fontSize = Mathf.RoundToInt(38 * scale),
                fontStyle = FontStyle.Bold,
                wordWrap = true,
            };
            _nameStyle.normal.textColor = Color.white;

            _blurbStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.UpperCenter,
                fontSize = Mathf.RoundToInt(22 * scale),
                wordWrap = true,
            };
            _blurbStyle.normal.textColor = Color.white;

            _tickStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = Mathf.RoundToInt(26 * scale),
                fontStyle = FontStyle.Bold,
            };
            _tickStyle.normal.textColor = Color.white;
        }
    }
}
