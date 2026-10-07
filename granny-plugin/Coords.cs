using System;
using UnityEngine;

namespace GrannyCraft
{
    /// <summary>
    /// Unity (left-handed, x right, y up, z forward) to Minecraft (x east, y up, z south; facing +Z, +X is on the left):
    /// MC = (-(x - ox) * S, y * S + oy, (z - oz) * S). Granny's house is built large (the player's eyes are ~3.7 units
    /// above the floor), so S (blocks per unit) is set from the eye height: Steve's eyes (1.62 blocks) sit at the camera.
    /// MC yaw = Unity yaw, MC pitch = Unity pitch. The offsets line Granny's floor up with a block boundary.
    /// </summary>
    internal static class Coords
    {
        public static double OX, OY = 64.0, OZ;
        public static double S = 0.434;

        public static Vector3d ToMc(Vector3 u) => new Vector3d(-(u.x - OX) * S, u.y * S + OY, (u.z - OZ) * S);

        public static Vector3 ToUnity(double x, double y, double z) => new Vector3((float)(-x / S + OX), (float)((y - OY) / S), (float)(z / S + OZ));

        /// <summary>Granny units per block.</summary>
        public static float U => (float)(1.0 / S);

        /// <summary>Unity centre of Minecraft block (bx, by, bz).</summary>
        public static Vector3 BlockCenter(int bx, int by, int bz) => ToUnity(bx + 0.5, by + 0.5, bz + 0.5);

        public static void YawPitch(Vector3 forward, out float yaw, out float pitch)
        {
            // Minecraft forward = (-sin yaw cos pitch, -sin pitch, cos yaw cos pitch), and mc.x = -unity.x
            double fx = -forward.x, fy = forward.y, fz = forward.z;
            yaw = (float)(Math.Atan2(-fx, fz) * 180.0 / Math.PI);
            pitch = (float)(-Math.Asin(Math.Clamp(fy, -1.0, 1.0)) * 180.0 / Math.PI);
        }

        /// <summary>Roll about the view axis, degrees (Minecraft's sign: positive tilts the picture clockwise).</summary>
        public static float Roll(Transform t)
        {
            Vector3 f = t.forward;
            Vector3 flatRight = Vector3.Cross(Vector3.up, f);
            if (flatRight.sqrMagnitude < 1e-6f) return 0f;
            flatRight.Normalize();
            Vector3 flatUp = Vector3.Cross(f, flatRight);
            float a = Mathf.Atan2(Vector3.Dot(t.up, -flatRight), Vector3.Dot(t.up, flatUp)) * Mathf.Rad2Deg;
            return a;
        }
    }

    internal struct Vector3d
    {
        public double x, y, z;
        public Vector3d(double x, double y, double z) { this.x = x; this.y = y; this.z = z; }
    }
}
