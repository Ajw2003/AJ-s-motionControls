using UnityEngine;

namespace Game
{
    /// <summary>
    /// The one place phone space becomes Unity space.
    /// </summary>
    /// <remarks>
    /// The phone is the rock. It sends the rotation from where the player was holding it
    /// when they tapped to where they are holding it now, as a quaternion, and this turns
    /// that into a Unity rotation that gets applied to the stone verbatim. Tilt the phone
    /// ten degrees and the stone tilts ten degrees.
    ///
    /// It used to be three angles. The phone decomposed its attitude into aim / attack /
    /// roll, and Unity re-composed them with two sign flips
    /// (<c>-message.aimDeg, -message.attackAngleDeg</c>) that were arrived at by trying
    /// both. That is why the stone never quite matched the phone: three chances to lose a
    /// degree of freedom, two hand-tuned negations, and a decomposition whose own neutral
    /// pose sat on a degenerate axis. One quaternion and one basis change has none of
    /// those failure modes — and if the basis change is wrong, every axis is wrong
    /// together and obviously, instead of one axis being subtly wrong.
    ///
    /// <b>If a rotation comes out mirrored, fix it here.</b> Never with a negation at a
    /// call site — that is how the old version got into this state.
    /// </remarks>
    public static class PhoneAttitude
    {
        /// <summary>
        /// A DeviceOrientation-frame rotation as a Unity rotation.
        /// </summary>
        /// <remarks>
        /// The device frame is right-handed: x runs right across the screen, y toward the
        /// top edge, z out through the glass. Unity is left-handed with y up and z forward.
        ///
        /// The neutral pose is the phone flat, screen up, top edge pointing at the TV, and
        /// it must produce a flat stone facing away from the camera. That pins the axis
        /// map: device x (right) is Unity x, device y (toward the top edge, so forward) is
        /// Unity z, and device z (out of the glass, so up) is Unity y — i.e.
        /// (x, y, z) -> (x, z, y).
        ///
        /// Swapping two axes flips handedness, which flips the sense of every rotation, so
        /// the vector part is negated as well as remapped. The scalar part is untouched:
        /// the angle is the same, only its direction reverses.
        /// </remarks>
        public static Quaternion ToUnity(float w, float x, float y, float z) =>
            // Unity's constructor takes (x, y, z, w), not (w, x, y, z).
            new Quaternion(-x, -z, -y, w);

        /// <summary>
        /// Which way a rotated stone points, flattened onto the water.
        /// </summary>
        /// <remarks>
        /// This is what the launch actually needs, so it is what the launch takes. It used
        /// to go through <see cref="HeadingDeg"/> and rebuild a direction from the angle
        /// with Quaternion.AngleAxis — an atan2 and a trig pair to recover a vector that
        /// was already in hand. Dropping the round trip also drops the only native call in
        /// the solver, which is what lets the whole thing run headlessly outside Unity.
        ///
        /// Straight up or straight down has no heading; fall back to forward rather than
        /// to a direction derived from floating-point noise.
        /// </remarks>
        public static Vector3 HeadingDirection(Quaternion attitude)
        {
            var forward = attitude * Vector3.forward;
            var flat = new Vector3(forward.x, 0f, forward.z);
            return flat.sqrMagnitude < 0.0001f ? Vector3.forward : flat.normalized;
        }

        /// <summary>
        /// Compass heading of a rotated stone, in degrees about world up. Readouts only —
        /// the launch uses <see cref="HeadingDirection"/>.
        /// </summary>
        /// <remarks>
        /// Read off the stone's own forward axis rather than from Euler angles, which
        /// would reintroduce exactly the gimbal problem the quaternion is here to avoid.
        /// </remarks>
        public static float HeadingDeg(Quaternion attitude)
        {
            var forward = attitude * Vector3.forward;
            var flat = new Vector3(forward.x, 0f, forward.z);

            return flat.sqrMagnitude < 0.0001f ? 0f : Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg;
        }

        /// <summary>
        /// Face angle of a rotated stone, in degrees above horizontal.
        /// </summary>
        /// <remarks>
        /// This is the number the bounce cares about, and it is measured from the stone's
        /// visible orientation — so what the player sees is what the water reacts to. A
        /// flat stone reads 0 here; the ~20 degrees a real stone wants is applied inside
        /// the bounce comparison, never to the stone itself.
        /// </remarks>
        public static float FaceAngleDeg(Quaternion attitude)
        {
            var forward = attitude * Vector3.forward;
            return Mathf.Asin(Mathf.Clamp(forward.y, -1f, 1f)) * Mathf.Rad2Deg;
        }

        /// <summary>
        /// Bank of a rotated stone about its own forward axis, in degrees. Positive rolls
        /// one way, negative the other; the sign convention lives here, not at call sites.
        /// </summary>
        /// <remarks>
        /// Computed against a "level" reference frame for the current heading/pitch — the
        /// right vector perpendicular to both world up and the stone's forward, and the up
        /// vector completing that level frame — rather than by decomposing Euler angles,
        /// which is the exact failure mode this file exists to avoid.
        /// </remarks>
        public static float RollDeg(Quaternion attitude)
        {
            var forward = attitude * Vector3.forward;
            var up = attitude * Vector3.up;

            var levelRight = Vector3.Cross(Vector3.up, forward);
            if (levelRight.sqrMagnitude < 0.0001f)
            {
                // Forward is (near) vertical; roll has no well-defined level reference.
                return 0f;
            }
            levelRight.Normalize();
            var levelUp = Vector3.Cross(forward, levelRight).normalized;

            return Vector3.SignedAngle(levelUp, up, forward);
        }

        /// <summary>
        /// The stone's face normal (the flat underside's outward direction) in world
        /// space. At the neutral pose this is straight up. Roll, pitch and yaw all show up
        /// here as one rotated vector, which is what the bounce should read rather than
        /// decomposed angles it would have to recompose.
        /// </summary>
        public static Vector3 FaceNormal(Quaternion attitude) => attitude * Vector3.up;
    }
}
