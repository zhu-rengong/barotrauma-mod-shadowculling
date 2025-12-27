using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Barotrauma;
using Microsoft.Xna.Framework;
using Whosyouradddy.ShadowCulling.Geometry;

namespace Whosyouradddy.ShadowCulling
{
    public class AABB
    {
        public static RectangleF Calculate(Character character)
        {
            RectangleF boundingBox = new(character.DrawPosition, Vector2.Zero);

            foreach (Limb limb in character.AnimController.Limbs)
            {
                if (limb.ActiveSprite == null) { continue; }
                float scale = limb.Scale * limb.TextureScale;
                float extentX = limb.ActiveSprite.size.X * scale * 0.5f;
                float extentY = limb.ActiveSprite.size.Y * scale * 0.5f;
                Vector2 drawPos = limb.DrawPosition;
                Vector2 origin = (limb.ActiveSprite.Origin - limb.ActiveSprite.SourceRect.Size.ToVector2() * 0.5f) * scale;
                float rotation = limb.body.Rotation;

                float sinRotation = MathF.Sin(rotation);
                float cosRotation = MathF.Cos(rotation);

                origin = new Vector2(
                    origin.X * cosRotation + origin.Y * sinRotation,
                    origin.X * sinRotation - origin.Y * cosRotation);
                boundingBox.AddDrawPointF(drawPos);
                Vector2 xExtend = new(extentX * cosRotation, extentX * sinRotation);
                Vector2 yExtend = new(extentY * sinRotation, -extentY * cosRotation);
                boundingBox.AddDrawPointF(drawPos + (xExtend + yExtend - origin));
                boundingBox.AddDrawPointF(drawPos + (xExtend - yExtend - origin));
                boundingBox.AddDrawPointF(drawPos + (-xExtend - yExtend - origin));
                boundingBox.AddDrawPointF(drawPos + (-xExtend + yExtend - origin));
            }

            boundingBox.X -= 25; boundingBox.Y += 25;
            boundingBox.Width += 50; boundingBox.Height += 50;

            return boundingBox;
        }
    }
}
