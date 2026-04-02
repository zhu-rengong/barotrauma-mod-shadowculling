using Microsoft.Xna.Framework;

namespace ShadowCulling
{
    internal static class RectangleFExtensions
    {
        public static void AddDrawPointF(ref this RectangleF rect, in Vector2 p)
        {
            if (p.X < rect.X)
            {
                rect.Width += rect.X - p.X;
                rect.X = p.X;
            }
            else if (p.X > rect.Right)
            {
                rect.Width += p.X - rect.Right;
            }

            if (p.Y > rect.Y)
            {
                rect.Height += p.Y - rect.Y;
                rect.Y = p.Y;
            }
            else if (p.Y < rect.Y - rect.Height)
            {
                rect.Height = rect.Y - p.Y;
            }
        }
    }
}
