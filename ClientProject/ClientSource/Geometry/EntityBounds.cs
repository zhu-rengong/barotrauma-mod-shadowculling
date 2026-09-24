namespace ShadowCulling;

/// <summary>
/// Builds the axis-aligned bounding box used to test whether a character is fully covered by shadows. Client-only:
/// it reads rendering data, so it cannot move into the shared project.
/// </summary>
public static class EntityBounds
{
    /// <summary>Calculates the bounding box of a character from its currently drawn limbs.</summary>
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
}
