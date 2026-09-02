using System.Collections.Generic;
using EventSystems;
using EventTypes.MatchEvents;
using Game;
using Net;
using Singleton;
using UnityEngine;

namespace Managers
{
    /// <summary>
    /// Owns what colour every player is. The only source of truth for "which dot is
    /// theirs" — everything downstream (the shore pointer today, a stone or a scoreboard
    /// row later) reads through here rather than deriving a colour of its own.
    /// </summary>
    /// <remarks>
    /// Modelled on <see cref="RockSelection"/>, which is the same shape of problem: a
    /// per-player choice a phone makes, held in a dictionary keyed by player id, seeded
    /// with a default the moment they join, and broadcast to every phone by TurnManager.
    ///
    /// <b>Keyed by identity, and nothing else.</b> This replaces the expression that used
    /// to live in ShoreRocks.CreateMarker:
    /// <code>PointerPalette[Mathf.Abs(playerId.GetHashCode() + ordinal) % Length]</code>
    /// where <c>ordinal</c> was how many pointers existed when this one was created. Every
    /// phase change destroys the pointers and clears that collection, so a player's colour
    /// depended on how many other people happened to have aimed before them that round —
    /// it moved between rounds, and <c>hash + ordinal</c> collided freely inside eight
    /// slots. Both complaints in notes.txt were that one expression.
    ///
    /// <b>A colour outlives a disconnect.</b> Unlike RockSelection, which drops a picked
    /// rock when a player leaves, a colour is held until the roster empties: a phone that
    /// drops and rejoins within the relay's grace window is the same player to everyone
    /// watching, and handing it a new colour would look exactly like the bug this
    /// replaces.
    ///
    /// <b>The host decides, not the phone.</b> A PICK_COLOUR is a request. See
    /// <see cref="Pick"/> for the two things it is checked against — legibility against
    /// the lake, and distance from the other players — both authored on
    /// <see cref="PlayerColourPalette"/>.
    /// </remarks>
    public class PlayerColours : SingletonBase<PlayerColours>
    {
        private PlayerColourPalette _palette;
        private RelayClient _relay;
        private readonly Dictionary<string, Color> _colours = new();

        /// <summary>The palette, for anything that needs the swatch list or its material knobs.</summary>
        public PlayerColourPalette Palette => _palette;

        protected override void Awake()
        {
            persistBetweenScenes = true;
            base.Awake();

            _palette = PlayerColourPalette.LoadOrWarn();

            EventManager.Instance?.Subscribe<PlayerJoinedEvent>(this, OnPlayerJoined);
            EventManager.Instance?.Subscribe<PlayerLeftEvent>(this, OnPlayerLeft);
        }

        /// <summary>
        /// Subscribes straight to RelayClient rather than waiting for something else to
        /// resolve the message, because unlike PICK_ROCK there is nothing to resolve — the
        /// phone sent the colour it wants. Bound from Update on the same
        /// wait-until-it-exists pattern TurnManager.BindRelay uses, so creation order
        /// between the two managers cannot drop the subscription.
        /// </summary>
        private void Update()
        {
            if (_relay != null || !RelayClient.HasInstance) return;

            _relay = RelayClient.Instance;
            _relay.PickColourReceived += OnPickColour;
        }

        private void OnPickColour(PickColourMessage message)
        {
            if (message == null) return;

            // An unparseable string is dropped rather than defaulted: a phone that sent
            // nonsense keeps the colour it already had, which is quieter than watching it
            // snap to black. ColorUtility wants the leading '#'.
            var text = string.IsNullOrEmpty(message.colour) ? null
                     : message.colour.StartsWith("#") ? message.colour
                     : "#" + message.colour;

            if (text == null || !ColorUtility.TryParseHtmlString(text, out var requested))
            {
                Debug.LogWarning($"[PlayerColours] {message.playerId} sent an unreadable colour: {message.colour}");
                return;
            }

            Pick(message.playerId, requested);
        }

        /// <summary>
        /// This player's colour: their pick, else the default assigned when they joined,
        /// else white for a player nobody has heard of yet.
        /// </summary>
        /// <remarks>
        /// Never returns a colour that depends on call order or on how many players are
        /// connected — that is the whole point of this class. Same "always answers"
        /// contract as <see cref="RockSelection.For"/>.
        /// </remarks>
        public Color For(string playerId)
        {
            if (!string.IsNullOrEmpty(playerId) && _colours.TryGetValue(playerId, out var colour))
            {
                return colour;
            }

            return Color.white;
        }

        /// <summary>Every player who has a colour, for the SPECTATE broadcast.</summary>
        public IEnumerable<KeyValuePair<string, Color>> All => _colours;

        /// <summary>
        /// A stone in this player's colour: their colour blended over the rock's own tint by
        /// <see cref="PlayerColourPalette.ConfirmedBlend"/>. Partway rather than flat, so the
        /// stone still looks like stone while answering "whose is that" across a room.
        /// </summary>
        /// <remarks>
        /// The one place that calculation lives. The shore stone a player claims, the stone
        /// they throw and the ghost they aim with all call this, so the three cannot drift
        /// apart — the rock you picked has to look like the rock you threw, and a second copy
        /// of the blend is how that would quietly stop being true.
        ///
        /// Returns <paramref name="baseColour"/> untouched when there is no palette, so a
        /// build without the asset degrades to the rock's authored tint rather than to
        /// nothing.
        /// </remarks>
        public Color StoneColourFor(string playerId, Color baseColour)
        {
            if (_palette == null) return baseColour;
            return Color.Lerp(baseColour, For(playerId), _palette.ConfirmedBlend);
        }

        /// <summary>
        /// Records a colour a phone asked for, after making it usable.
        /// </summary>
        /// <remarks>
        /// Accepted in any phase, unlike <see cref="RockSelection.Pick"/>. A rock pick
        /// arriving mid-throw is stale and has to be refused; a colour change never is —
        /// it is cosmetic, it cannot affect a throw in flight, and a player who wants a
        /// different colour while watching somebody else throw should simply get one.
        ///
        /// Two adjustments, in order, both authored on the palette asset:
        /// <list type="number">
        /// <item>raise value/saturation to the floors, so the dot reads against dark water;</item>
        /// <item>rotate hue away from any connected player within MinSeparation, so no two
        /// dots on the TV are confusable.</item>
        /// </list>
        /// Nudged rather than refused, because a refusal the phone cannot see just looks
        /// broken — the settled colour goes back out in the next SPECTATE, so the picker
        /// shows what the player actually got.
        /// </remarks>
        public void Pick(string playerId, Color requested)
        {
            if (string.IsNullOrEmpty(playerId)) return;

            var settled = Separate(playerId, Legible(requested));

            if (_colours.TryGetValue(playerId, out var current) && current == settled) return;

            _colours[playerId] = settled;
            EventManager.Instance?.Publish(new PlayerColourChangedEvent(playerId, settled));
        }

        private void OnPlayerJoined(PlayerJoinedEvent e)
        {
            // A reconnect fires this again for a player who already has a colour. Leaving
            // it is the point — see the class remarks.
            if (_colours.ContainsKey(e.PlayerId)) return;

            var taken = new List<Color>(_colours.Values);
            var assigned = _palette.FurthestFrom(taken);

            _colours[e.PlayerId] = assigned;
            EventManager.Instance?.Publish(new PlayerColourChangedEvent(e.PlayerId, assigned));
        }

        private void OnPlayerLeft(PlayerLeftEvent e)
        {
            // Deliberately not removed: a dropped phone is usually seconds from coming
            // back, and its colour must survive the round trip. Cleared only once nobody
            // is left, which is the same point TurnManager treats as the end of a session.
            if (TurnManager.HasInstance && TurnManager.Instance.Players.Count > 0) return;

            _colours.Clear();
        }

        /// <summary>Raises a colour to the palette's brightness and saturation floors.</summary>
        private Color Legible(Color requested)
        {
            Color.RGBToHSV(requested, out var h, out var s, out var v);
            return Color.HSVToRGB(
                h,
                Mathf.Max(s, _palette.MinSaturation),
                Mathf.Max(v, _palette.MinValue));
        }

        /// <summary>
        /// Rotates hue away until no other connected player is within MinSeparation, or
        /// gives up and hands back the furthest free swatch.
        /// </summary>
        /// <remarks>
        /// Steps in twelfths of the hue wheel: fine enough that the player still recognises
        /// what they asked for, coarse enough to clear a neighbour in a step or two. The
        /// loop is bounded — a full turn without finding a gap means the palette cannot
        /// separate this many players, and the swatch fallback is the honest answer.
        /// </remarks>
        private Color Separate(string playerId, Color colour)
        {
            if (_palette.MinSeparation <= 0f) return colour;

            var others = new List<Color>();
            foreach (var pair in _colours)
            {
                if (pair.Key != playerId) others.Add(pair.Value);
            }

            if (others.Count == 0) return colour;

            Color.RGBToHSV(colour, out var h, out var s, out var v);

            for (var step = 0; step < 12; step++)
            {
                var candidate = Color.HSVToRGB(Mathf.Repeat(h + step / 12f, 1f), s, v);

                var clear = true;
                foreach (var other in others)
                {
                    if (PlayerColourPalette.Distance(candidate, other) >= _palette.MinSeparation) continue;
                    clear = false;
                    break;
                }

                if (clear) return candidate;
            }

            return _palette.FurthestFrom(others);
        }

        protected override void OnDestroy()
        {
            if (_relay != null) _relay.PickColourReceived -= OnPickColour;
            EventManager.Instance?.UnsubscribeFromAllEvents(this);
            base.OnDestroy();
        }
    }
}
