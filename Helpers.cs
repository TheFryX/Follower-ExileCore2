using System;
using System.Diagnostics;
using System.Numerics;
using ExileCore2.Shared;

namespace FollowerInternals
{
    public static class MathEx
    {
        // Classic smoothstep on vectors
        public static Vector2 SmoothStep(Vector2 from, Vector2 to, float t)
        {
            t = Math.Clamp(t, 0f, 1f);
            // cubic hermite: t*t*(3 - 2*t)
            var s = t * t * (3f - 2f * t);
            return from + (to - from) * s;
        }

        public static Vector3 SmoothStep(Vector3 from, Vector3 to, float t)
        {
            t = Math.Clamp(t, 0f, 1f);
            var s = t * t * (3f - 2f * t);
            return from + (to - from) * s;
        }

        // Placeholders: if original code used GridToWorld/WorldToGrid, keep identity mapping
        // You can wire these to ExileCore2 terrain utils later if needed.
        public static Vector2 WorldToGrid(Vector3 world) => new Vector2(world.X, world.Y);
        public static Vector3 GridToWorld(Vector2 grid, float z = 0f) => new Vector3(grid.X, grid.Y, z);
    }

    // Simple helper to mimic old WaitTime behavior
    public sealed class WaitTime
    {
        readonly int _ms;
        readonly Stopwatch _sw = new Stopwatch();
        public WaitTime(int milliseconds) { _ms = Math.Max(0, milliseconds); }
        public void Restart() { _sw.Restart(); }
        public bool IsOver() => _sw.ElapsedMilliseconds >= _ms;
        public void WaitBlocking()
        {
            var remain = _ms - (int)_sw.ElapsedMilliseconds;
            if (remain > 0) System.Threading.Thread.Sleep(remain);
            _sw.Restart();
        }
    }

    public static class RectEx
    {
        public static RectangleF ToHudRect(System.Drawing.RectangleF r) => new RectangleF(r.X, r.Y, r.Width, r.Height);
    }
}
