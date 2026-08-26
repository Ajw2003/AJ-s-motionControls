using System.Collections.Generic;
using System.Linq;
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
    /// Active only while <see cref="MatchPhase.Lobby"/> is current — this is the other half
    /// of the fix the plan's constraints call for: AimPreview only reacts to AIM while
    /// Playing, this only reacts while in the Lobby, so the two never fight over the same
    /// frame. Built by AimPreview alongside the placeholder water, sharing its throw
    /// origin and camera framing — see <see cref="Bind"/>.
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

        private class Stone
        {
            public RockDefinition Definition;
            public Transform Transform;
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
            var inLobby = !TurnManager.HasInstance || TurnManager.Instance.Phase == MatchPhase.Lobby;
            SetVisible(inLobby);
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
            EnsureBound();
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

        private void EnsureBound()
        {
            if (_client != null || !RelayClient.HasInstance) return;

            _client = RelayClient.Instance;
            _client.AimReceived += OnAim;
            _client.PickRockReceived += OnPickRock;
        }

        private void SetVisible(bool visible)
        {
            if (gameObject.activeSelf != visible) gameObject.SetActive(visible);
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
            if (!TurnManager.HasInstance || TurnManager.Instance.Phase != MatchPhase.Lobby) return;
            if (string.IsNullOrEmpty(aim.playerId)) return;

            var attitude = PhoneAttitude.ToUnity(aim.qw, aim.qx, aim.qy, aim.qz);
            var yaw = PhoneAttitude.HeadingDeg(attitude);

            if (!_pointers.TryGetValue(aim.playerId, out var pointer))
            {
                pointer = new Pointer { TargetYawDeg = yaw, SmoothedYawDeg = yaw };
                pointer.Marker = CreateMarker(aim.playerId, _pointers.Count);
                _pointers[aim.playerId] = pointer;
            }

            pointer.TargetYawDeg = yaw;
            pointer.LastAimTime = Time.time;
        }

        private void OnPickRock(PickRockMessage message)
        {
            if (!TurnManager.HasInstance || TurnManager.Instance.Phase != MatchPhase.Lobby) return;
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
                pointer.Marker = CreateMarker(message.playerId, _pointers.Count);
                _pointers[message.playerId] = pointer;
            }

            pointer.Confirmed = true;
            pointer.ConfirmedIndex = index;
        }

        private void OnPlayerLeft(PlayerLeftEvent e) => RemovePointer(e.PlayerId);

        private void OnMatchPhaseChanged(MatchPhaseChangedEvent e)
        {
            var enteringLobby = e.To == MatchPhase.Lobby;
            SetVisible(enteringLobby);

            if (!enteringLobby) return;

            // Every new match re-picks from scratch, same timing as RockSelection's own
            // clear — see its OnMatchPhaseChanged.
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

                var confirmedHere = _pointers.Values.Any(p => p.Confirmed && p.ConfirmedIndex == index);
                var highlightedHere = !confirmedHere && _pointers.Values.Any(p => p.HighlightIndex == index);

                stone.Material.color = confirmedHere ? _tuning.ConfirmedColour
                    : highlightedHere ? _tuning.HighlightColour
                    : stone.BaseColour;

                stone.Transform.localScale = stone.BaseScale * (confirmedHere || highlightedHere ? _tuning.HighlightScale : 1f);
            }
        }

        // ------------------------------------------------------------------ pointer markers

        private static readonly Color[] PointerPalette =
        {
            new(0.95f, 0.35f, 0.35f), new(0.35f, 0.75f, 0.95f), new(0.95f, 0.85f, 0.35f),
            new(0.55f, 0.9f, 0.55f), new(0.85f, 0.5f, 0.95f), new(0.95f, 0.6f, 0.3f),
            new(0.6f, 0.8f, 0.9f), new(0.9f, 0.9f, 0.9f),
        };

        private Transform CreateMarker(string playerId, int ordinal)
        {
            var marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            marker.name = $"Pointer_{playerId}";
            Destroy(marker.GetComponent<Collider>());

            var material = PlaceholderArt.Create(
                PointerPalette[Mathf.Abs(playerId.GetHashCode() + ordinal) % PointerPalette.Length],
                PlaceholderArt.StoneQueue);
            marker.GetComponent<MeshRenderer>().material = material;

            marker.transform.SetParent(transform, false);
            marker.transform.localScale = Vector3.one * 0.35f;
            return marker.transform;
        }

        // ------------------------------------------------------------------ labels

        private void OnGUI()
        {
            if (!gameObject.activeSelf) return;

            var camera = Camera.main;
            if (camera == null) return;

            EnsureStyles();

            foreach (var stone in _stones)
            {
                DrawWorldLabel(camera, stone.Transform.position + Vector3.up * 0.8f,
                    stone.Definition.DisplayName, _nameStyle);

                if (!string.IsNullOrEmpty(stone.Definition.Blurb))
                {
                    DrawWorldLabel(camera, stone.Transform.position + Vector3.up * 0.5f,
                        stone.Definition.Blurb, _blurbStyle);
                }
            }

            foreach (var pair in _pointers)
            {
                if (!pair.Value.Confirmed) continue;

                var index = pair.Value.ConfirmedIndex;
                if (index < 0 || index >= _stones.Length) continue;

                DrawWorldLabel(camera, _stones[index].Transform.position + Vector3.up * 1.1f,
                    $"✓ {pair.Key}", _tickStyle);
            }
        }

        private static void DrawWorldLabel(Camera camera, Vector3 worldPosition, string text, GUIStyle style)
        {
            var screen = camera.WorldToScreenPoint(worldPosition);
            if (screen.z <= 0f) return;

            var rect = new Rect(screen.x - 80f, Screen.height - screen.y - 20f, 160f, 24f);
            GUI.Label(rect, text, style);
        }

        private void EnsureStyles()
        {
            if (_nameStyle != null) return;

            _nameStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 16,
                fontStyle = FontStyle.Bold,
            };
            _nameStyle.normal.textColor = Color.white;

            _blurbStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 12,
            };
            _blurbStyle.normal.textColor = new Color(0.85f, 0.85f, 0.85f);

            _tickStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 14,
                fontStyle = FontStyle.Bold,
            };
            _tickStyle.normal.textColor = new Color(0.4f, 0.9f, 0.5f);
        }
    }
}
