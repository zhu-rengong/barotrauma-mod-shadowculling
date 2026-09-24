using ConvexHull = Barotrauma.Lights.ConvexHull;

namespace ShadowCulling;

/// <summary>Represents a shadow cast by a convex hull from a light source.</summary>
public struct Shadow : IEquatable<Shadow>
{
    public readonly ConvexHull ConvexHull;
    public Vector2 LightSource;
    public Segment Occluder;
    public Ray2D Ray1;
    public Ray2D Ray2;
    public float RayScanDir;
    public float DistanceToView;
    public Quadrant OccluderQuadrants;

    /// <summary>Initializes a new instance of the <see cref="Shadow"/> struct.</summary>
    public Shadow(ConvexHull convexHull, Vector2 lightSource, Vector2 vertex1, Vector2 vertex2)
    {
        ConvexHull = convexHull;
        LightSource = lightSource;
        Occluder = new(vertex1, vertex2);
        Ray1 = new(vertex1, vertex1 - lightSource);
        Ray2 = new(vertex2, vertex2 - lightSource);
        CalculateProperties();
    }

    /// <summary>Calculates the properties of the shadow.</summary>
    public void CalculateProperties()
    {
        RayScanDir = Ray1.Direction.CrossProduct(Ray2.Direction);
    }

    /// <summary>Recalculates the shadow with new parameters.</summary>
    public void Recalculate(in Vector2 lightSource, in Vector2 vertex1, in Vector2 vertex2)
    {
        LightSource.X = lightSource.X;
        LightSource.Y = lightSource.Y;

        Occluder.Start.X = vertex1.X;
        Occluder.Start.Y = vertex1.Y;
        Occluder.End.X = vertex2.X;
        Occluder.End.Y = vertex2.Y;
        Occluder.CalculateProperties();

        Ray1.Origin.X = vertex1.X;
        Ray1.Origin.Y = vertex1.Y;
        Ray1.Direction.X = vertex1.X - lightSource.X;
        Ray1.Direction.Y = vertex1.Y - lightSource.Y;
        Ray1.NormalizeDirection();

        Ray2.Origin.X = vertex2.X;
        Ray2.Origin.Y = vertex2.Y;
        Ray2.Direction.X = vertex2.X - lightSource.X;
        Ray2.Direction.Y = vertex2.Y - lightSource.Y;
        Ray2.NormalizeDirection();

        CalculateProperties();
    }

    public override readonly int GetHashCode()
    {
        return HashCode.Combine(LightSource, Occluder);
    }

    public override readonly bool Equals(object? obj) => obj is Shadow other && this == other;

    public readonly bool Equals(Shadow other) => LightSource == other.LightSource && Occluder == other.Occluder;

    public static bool operator ==(in Shadow left, in Shadow right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(in Shadow left, in Shadow right)
    {
        return !(left == right);
    }
}
