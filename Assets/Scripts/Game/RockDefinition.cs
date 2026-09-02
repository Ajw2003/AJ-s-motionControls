using UnityEngine;

namespace Game
{
    /// <summary>
    /// One stone: what it looks like, what it is called, and how it skips.
    /// </summary>
    /// <remarks>
    /// Resources/Fbx/RocksRemeshed.fbx has held six meshes since it was imported and the
    /// game has only ever drawn one of them — RockObject.prefab pins a single mesh by
    /// fileID and nothing in the scripts has ever chosen between them. This is what turns
    /// the other five into rocks rather than unused sub-assets.
    ///
    /// A rock is a mesh AND a tuning, and the pairing is the whole feature. A flat stone
    /// that skips exactly like a chunky one is a reskin, not a choice.
    ///
    /// One tuning, not two. There used to be an Authored/Derived pair compared against
    /// each other with real match data — see docs/Findings-RockSeparation.md and
    /// docs/plans/Plan-ThrowDrivenSkipModel.md for why that A/B apparatus is gone: rock
    /// character is now three explicit multipliers, and there is nothing left to compare.
    ///
    /// Built and maintained by Editor/RockCatalogBuilder. Hand edits to
    /// <see cref="DisplayName"/>, <see cref="Blurb"/> and <see cref="Tint"/> survive a
    /// rebuild; the mesh reference and scale are recomputed from the FBX.
    /// </remarks>
    [CreateAssetMenu(menuName = "RockSkipping/Rock Definition", fileName = "Rock")]
    public class RockDefinition : ScriptableObject
    {
        [Header("Identity")]
        [Tooltip("Stable id. Goes on the wire, into the telemetry CSV and into logs, so it " +
                 "must not change once matches have been recorded against it — rename the " +
                 "DisplayName instead.")]
        public string Id = "rock";

        [Tooltip("What the TV and the phones call this stone.")]
        public string DisplayName = "Pebble";

        [Tooltip("One line under the stone on the shore. Flavour, not stats — the numbers " +
                 "are not legible from a couch and pretending otherwise just adds clutter.")]
        public string Blurb = string.Empty;

        [Header("Look")]
        [Tooltip("A mesh out of Resources/Fbx/RocksRemeshed.fbx. Assigned by the catalog " +
                 "builder; the FBX is not readable, so only bounds are available from it.")]
        public Mesh Mesh;

        [Tooltip("Scale that brings this mesh to a common footprint. Computed by the catalog " +
                 "builder rather than authored: the six meshes come out of Blender at wildly " +
                 "different sizes, and without normalising them the set reads as one rock and " +
                 "five mistakes. Height scales with the footprint so genuine thickness " +
                 "differences survive — that difference is exactly what the derived tunings " +
                 "key off.")]
        public Vector3 LocalScale = Vector3.one;

        [Tooltip("Correction applied to the mesh so its flat face lies in the stone's face " +
                 "plane. Zero for a mesh that already imports flat.\n\n" +
                 "This has to be right or the stone lies about its own angle: the bounce reads " +
                 "the face angle off the transform, and RockTrail aligns the ribbon to the same " +
                 "plane — a mesh rotated 90 degrees out would show a stone visibly on edge " +
                 "while the solver treats it as flat.")]
        public Vector3 MeshRotation = Vector3.zero;

        [Tooltip("Colour of the live stone and of this rock on the shore. NOT applied to the " +
                 "aim ghost — the ghost's colour is charge feedback and already means " +
                 "something else. See AimPreview.UpdateGhostCharge.")]
        public Color Tint = new(0.45f, 0.43f, 0.43f);

        [Header("Physics")]
        [Tooltip("Three bounded multipliers over the geometry-driven bounce. " +
                 "Assets/Rocks/Tuning/Rock_0N.asset.")]
        public SkipTuning Tuning;

        /// <summary>Whether this rock can actually be played.</summary>
        public bool IsUsable => Mesh != null && Tuning != null;
    }
}
