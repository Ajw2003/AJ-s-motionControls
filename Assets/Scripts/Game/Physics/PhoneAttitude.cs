using UnityEngine;

namespace Game
{
    // Phone attitude is one quaternion and one basis change; if a rotation comes out mirrored, fix it
    // here, never with a negation at a call site. doc-ref f406 docs/4-systems/MotionPipeline.md
    public static class PhoneAttitude
    {
        // The axis map (x,y,z) to (x,z,y) flips handedness, so the vector part is negated and the
        // scalar part is left alone. doc-ref 12a1 docs/4-systems/MotionPipeline.md
        public static Quaternion ToUnity(float w, float x, float y, float z) =>
            // Unity's constructor takes (x, y, z, w), not (w, x, y, z).
            new Quaternion(-x, -z, -y, w);

        // Heading comes straight from the rotated forward vector, with forward as the fallback when the
        // stone points straight up or down. doc-ref 268d docs/4-systems/MotionPipeline.md
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
