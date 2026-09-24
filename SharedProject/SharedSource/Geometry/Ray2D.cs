namespace ShadowCulling;

/// <summary>
/// Represents a 2D ray with an origin and direction. Named <c>Ray2D</c> rather than <c>Ray</c> to stay unambiguous
/// with <c>Microsoft.Xna.Framework.Ray</c>, which is globally imported.
/// </summary>
public struct Ray2D
{
    public Vector2 Origin;
    public Vector2 Direction;

    /// <summary>Initializes a new instance of the <see cref="Ray2D"/> struct.</summary>
    public Ray2D(in Vector2 origin, in Vector2 direction)
    {
        Origin = origin;
        Direction = direction;
        NormalizeDirection();
    }

    /// <summary>Normalizes the direction vector of the ray.</summary>
    public void NormalizeDirection()
    {
        if (Direction != Vector2.Zero)
        {
            Direction.Normalize();
        }
    }
}
