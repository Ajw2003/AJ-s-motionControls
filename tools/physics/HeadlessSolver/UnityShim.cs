using System;

// Minimal managed stand-ins for the handful of UnityEngine maths types the physics core
// touches, so the REAL physics files compile and run on plain dotnet.
//
// Why a shim rather than referencing UnityEngine.CoreModule.dll: several Quaternion members
// (Inverse, FromToRotation, AngleAxis, Euler) are native ECalls into the Unity player. The
// DLL loads fine but throws "ECall methods must be packaged into a system module" the moment
// one is called. Everything the physics core needs is ordinary maths, so it is reimplemented
// here from Unity's own documented formulas.
//
// These MUST match Unity's conventions exactly or the harness would validate a solver that
// is not the one that ships. `SelfTest()` checks that, and Program runs it before anything
// else — see the assertions there for what "matching" is pinned to.
namespace UnityEngine
{
    public static class Mathf
    {
        public const float PI = 3.14159274f;
        public const float Deg2Rad = PI / 180f;
        public const float Rad2Deg = 180f / PI;

        public static float Sqrt(float f) => (float)Math.Sqrt(f);
        public static float Acos(float f) => (float)Math.Acos(f);
        public static float Asin(float f) => (float)Math.Asin(f);
        public static float Cos(float f) => (float)Math.Cos(f);
        public static float Sin(float f) => (float)Math.Sin(f);
        public static float Atan2(float y, float x) => (float)Math.Atan2(y, x);
        public static float Pow(float f, float p) => (float)Math.Pow(f, p);
        public static float Abs(float f) => Math.Abs(f);
        public static float Max(float a, float b) => a > b ? a : b;
        public static float Min(float a, float b) => a < b ? a : b;
        public static int Max(int a, int b) => a > b ? a : b;

        public static float Clamp(float value, float min, float max) =>
            value < min ? min : (value > max ? max : value);

        public static float Clamp01(float value) => Clamp(value, 0f, 1f);
        public static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);
        public static int CeilToInt(float f) => (int)Math.Ceiling(f);
    }

    public struct Vector3
    {
        public float x, y, z;

        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }

        public static Vector3 zero => new Vector3(0f, 0f, 0f);
        public static Vector3 up => new Vector3(0f, 1f, 0f);
        public static Vector3 down => new Vector3(0f, -1f, 0f);
        public static Vector3 right => new Vector3(1f, 0f, 0f);
        public static Vector3 forward => new Vector3(0f, 0f, 1f);

        public float sqrMagnitude => x * x + y * y + z * z;
        public float magnitude => Mathf.Sqrt(sqrMagnitude);

        public Vector3 normalized
        {
            get
            {
                var m = magnitude;
                // Unity returns the zero vector below its 1e-5 epsilon rather than NaN.
                return m > 1e-5f ? new Vector3(x / m, y / m, z / m) : zero;
            }
        }

        public void Normalize() { this = normalized; }

        public static float Dot(Vector3 a, Vector3 b) => a.x * b.x + a.y * b.y + a.z * b.z;

        public static Vector3 Cross(Vector3 a, Vector3 b) => new Vector3(
            a.y * b.z - a.z * b.y,
            a.z * b.x - a.x * b.z,
            a.x * b.y - a.y * b.x);

        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3 operator -(Vector3 a) => new Vector3(-a.x, -a.y, -a.z);
        public static Vector3 operator *(Vector3 a, float d) => new Vector3(a.x * d, a.y * d, a.z * d);
        public static Vector3 operator *(float d, Vector3 a) => a * d;
        public static Vector3 operator /(Vector3 a, float d) => new Vector3(a.x / d, a.y / d, a.z / d);

        public override string ToString() => $"({x:F4}, {y:F4}, {z:F4})";
    }

    public struct Quaternion
    {
        public float x, y, z, w;

        public Quaternion(float x, float y, float z, float w)
        {
            this.x = x; this.y = y; this.z = z; this.w = w;
        }

        public static Quaternion identity => new Quaternion(0f, 0f, 0f, 1f);

        /// <summary>Unity's Hamilton product, operand order and all.</summary>
        public static Quaternion operator *(Quaternion lhs, Quaternion rhs) => new Quaternion(
            lhs.w * rhs.x + lhs.x * rhs.w + lhs.y * rhs.z - lhs.z * rhs.y,
            lhs.w * rhs.y + lhs.y * rhs.w + lhs.z * rhs.x - lhs.x * rhs.z,
            lhs.w * rhs.z + lhs.z * rhs.w + lhs.x * rhs.y - lhs.y * rhs.x,
            lhs.w * rhs.w - lhs.x * rhs.x - lhs.y * rhs.y - lhs.z * rhs.z);

        /// <summary>Rotate a point. Transcribed from Unity's own implementation.</summary>
        public static Vector3 operator *(Quaternion rotation, Vector3 point)
        {
            float x2 = rotation.x * 2f, y2 = rotation.y * 2f, z2 = rotation.z * 2f;
            float xx = rotation.x * x2, yy = rotation.y * y2, zz = rotation.z * z2;
            float xy = rotation.x * y2, xz = rotation.x * z2, yz = rotation.y * z2;
            float wx = rotation.w * x2, wy = rotation.w * y2, wz = rotation.w * z2;

            return new Vector3(
                (1f - (yy + zz)) * point.x + (xy - wz) * point.y + (xz + wy) * point.z,
                (xy + wz) * point.x + (1f - (xx + zz)) * point.y + (yz - wx) * point.z,
                (xz - wy) * point.x + (yz + wx) * point.y + (1f - (xx + yy)) * point.z);
        }

        /// <summary>Conjugate over squared norm — the true inverse, not the unit shortcut,
        /// so a drifting quaternion behaves the same here as in Unity.</summary>
        public static Quaternion Inverse(Quaternion r)
        {
            var n = r.x * r.x + r.y * r.y + r.z * r.z + r.w * r.w;
            if (n < 1e-12f) return identity;
            return new Quaternion(-r.x / n, -r.y / n, -r.z / n, r.w / n);
        }

        public static Quaternion AngleAxis(float angleDeg, Vector3 axis)
        {
            var a = axis.normalized;
            var half = angleDeg * Mathf.Deg2Rad * 0.5f;
            var s = Mathf.Sin(half);
            return new Quaternion(a.x * s, a.y * s, a.z * s, Mathf.Cos(half));
        }

        public static Quaternion FromToRotation(Vector3 from, Vector3 to)
        {
            var f = from.normalized;
            var t = to.normalized;
            var d = Mathf.Clamp(Vector3.Dot(f, t), -1f, 1f);

            if (d > 0.999999f) return identity;

            if (d < -0.999999f)
            {
                // Antiparallel: any perpendicular axis is a valid 180-degree rotation.
                var axis = Vector3.Cross(Vector3.right, f);
                if (axis.sqrMagnitude < 1e-6f) axis = Vector3.Cross(Vector3.up, f);
                return AngleAxis(180f, axis.normalized);
            }

            return AngleAxis(Mathf.Acos(d) * Mathf.Rad2Deg, Vector3.Cross(f, t));
        }

        public override string ToString() => $"({x:F4}, {y:F4}, {z:F4}, {w:F4})";

        /// <summary>
        /// Pins the conventions this shim must share with Unity. Program calls it before
        /// running anything, because a silently-wrong convention here would calibrate a
        /// solver that is not the one that ships.
        /// </summary>
        public static void SelfTest()
        {
            void Near(string what, float got, float want)
            {
                if (Mathf.Abs(got - want) > 1e-4f)
                    throw new Exception($"UnityShim.SelfTest FAILED: {what} = {got}, expected {want}");
            }

            // 90 degrees about +X takes +Y to +Z (Unity's left-handed sense).
            var q = AngleAxis(90f, Vector3.right);
            var v = q * Vector3.up;
            Near("AngleAxis(90,X) * up .y", v.y, 0f);
            Near("AngleAxis(90,X) * up .z", v.z, 1f);

            // Inverse undoes the rotation.
            var back = Inverse(q) * v;
            Near("Inverse round trip .y", back.y, 1f);

            // FromToRotation actually maps from onto to.
            var target = new Vector3(0f, Mathf.Cos(0.35f), -Mathf.Sin(0.35f)).normalized;
            var mapped = FromToRotation(Vector3.up, target) * Vector3.up;
            Near("FromToRotation .y", mapped.y, target.y);
            Near("FromToRotation .z", mapped.z, target.z);

            // Cross follows the left-handed convention: X cross Y = Z.
            var c = Vector3.Cross(Vector3.right, Vector3.up);
            Near("cross(right,up).z", c.z, 1f);

            // Composition order: (a*b) applies b first, then a.
            var a90 = AngleAxis(90f, Vector3.up);
            var composed = (a90 * a90) * Vector3.forward;
            Near("double yaw .z", composed.z, -1f);
        }
    }
}
