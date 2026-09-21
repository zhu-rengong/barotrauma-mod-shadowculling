namespace ShadowCulling;

/// <summary>
/// Represents a range between two rays, useful for sector-based spatial queries. A value type because four of these
/// are probed per convex hull and again per entity of every cull, so passing them by <c>in</c> reference is worth the
/// copy on write.
/// </summary>
public struct RayRange
{
    public Vector2 Origin;
    public Ray2D Start;
    public Ray2D End;
    public float RayScanDir;

    /// <summary>Initializes a new instance of the <see cref="RayRange"/> struct.</summary>
    public RayRange(in Vector2 origin, in Vector2 startDirection, in Vector2 endDirection)
    {
        Origin = origin;
        Start = new(origin, startDirection);
        End = new(origin, endDirection);
        CalculateProperties();
    }

    /// <summary>Calculates the properties of the ray range.</summary>
    public void CalculateProperties()
    {
        RayScanDir = Start.Direction.CrossProduct(End.Direction);
    }

    /// <summary>Updates the origin of the ray range.</summary>
    public void UpdateOrigin(in Vector2 newOrigin)
    {
        Origin.X = newOrigin.X;
        Origin.Y = newOrigin.Y;
        Start.Origin.X = newOrigin.X;
        Start.Origin.Y = newOrigin.Y;
        End.Origin.X = newOrigin.X;
        End.Origin.Y = newOrigin.Y;
    }
}
