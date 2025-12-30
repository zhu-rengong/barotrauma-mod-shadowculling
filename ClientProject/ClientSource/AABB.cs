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
        public static RectangleF CalculateDynamic(Character character)
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

        public static RectangleF CalculateFixed(Structure structure)
        {
            RectangleF boundingBox = Quad2D.FromSubmarineRectangle(structure.WorldRect).Rotated(
                structure.FlippedX != structure.FlippedY
                    ? structure.RotationRad
                    : -structure.RotationRad).BoundingAxisAlignedRectangle;

            Vector2 min = new Vector2(-boundingBox.Width / 2, -boundingBox.Height / 2);
            Vector2 max = -min;

            foreach (DecorativeSprite decorativeSprite in structure.Prefab.DecorativeSprites)
            {
                Vector2 scale = decorativeSprite.GetScale(
                    ref structure.spriteAnimState[decorativeSprite].ScaleState,
                    structure.spriteAnimState[decorativeSprite].RandomScaleFactor)
                    * structure.Scale;
                min.X = Math.Min(-decorativeSprite.Sprite.size.X * decorativeSprite.Sprite.RelativeOrigin.X * scale.X, min.X);
                min.Y = Math.Min(-decorativeSprite.Sprite.size.Y * (1.0f - decorativeSprite.Sprite.RelativeOrigin.Y) * scale.Y, min.Y);
                max.X = Math.Max(decorativeSprite.Sprite.size.X * (1.0f - decorativeSprite.Sprite.RelativeOrigin.X) * scale.X, max.X);
                max.Y = Math.Max(decorativeSprite.Sprite.size.Y * decorativeSprite.Sprite.RelativeOrigin.Y * scale.Y, max.Y);
            }

            boundingBox.X = min.X;
            boundingBox.Y = max.Y;
            boundingBox.Width = max.X - min.X;
            boundingBox.Height = max.Y - min.Y;

            return boundingBox;
        }
    }
}
