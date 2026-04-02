using Barotrauma;
using Microsoft.Xna.Framework;

namespace ShadowCulling
{
    internal static class Vector2Extensions
    {
        public static float CrossProduct(in this Vector2 a, in Vector2 b)
        {
            return a.X * b.Y - a.Y * b.X;
        }

        public static float VectorAngle(in this Vector2 p1, in Vector2 p2)
        {
            return MathUtils.WrapAnglePi(MathF.Atan2(p1.Y, p1.X) - MathF.Atan2(p2.Y, p2.X));
        }
    }
}
