using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Barotrauma;
using Microsoft.Xna.Framework;
using ConvexHull = Barotrauma.Lights.ConvexHull;

namespace ShadowCulling.Geometry;

/// <summary>
/// Defines the four quadrants and combinations for spatial partitioning.
/// </summary>
[Flags]
public enum Quadrant
{
    None = 0,
    RightTop = 0x01,
    LeftTop = 0x02,
    LeftBottom = 0x04,
    RightBottom = 0x08,
    Top = RightTop | LeftTop,
    Left = LeftTop | LeftBottom,
    Bottom = RightBottom | LeftBottom,
    Right = RightTop | RightBottom,
    All = RightTop | LeftTop | LeftBottom | RightBottom,
}

/// <summary>
/// Represents a 2D line segment with intersection and clipping capabilities.
/// </summary>
public struct Segment
{
    public Vector2 Start;
    public Vector2 End;
    public Vector2 StartToEnd;
    public Vector2 Center;
    public float LengthSquared;
    public float Length;
    private int _hashCode;

    /// <summary>
    /// Initializes a new instance of the <see cref="Segment"/> struct.
    /// </summary>
    /// <param name="start">The starting point of the segment.</param>
    /// <param name="end">The ending point of the segment.</param>
    public Segment(in Vector2 start, in Vector2 end)
    {
        Start = start;
        End = end;
        CalculateProperties();
    }

    /// <summary>
    /// Calculates the derived properties of the segment.
    /// </summary>
    public void CalculateProperties()
    {
        StartToEnd = End - Start;
        Center.X = (Start.X + End.X) * 0.5f;
        Center.Y = (Start.Y + End.Y) * 0.5f;
        LengthSquared = StartToEnd.LengthSquared();
        Length = (float)Math.Sqrt(LengthSquared);
        _hashCode = HashCode.Combine(Start, End);
    }

    /// <summary>
    /// Attempts to find the intersection point with a ray.
    /// </summary>
    /// <param name="ray">The ray to intersect with.</param>
    /// <param name="intersection">The intersection point if found.</param>
    /// <returns>True if an intersection exists, false otherwise.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetIntersection(in Ray ray, ref Vector2 intersection)
    {
        ref readonly Vector2 rayOrigin = ref ray.Origin;
        ref readonly Vector2 rayDirection = ref ray.Direction;

        float denominator = StartToEnd.CrossProduct(rayDirection);

        // Segment and ray are collinear
        if (Math.Abs(denominator) < 1e-4f) { return false; }

        // Assuming segment and ray have an intersection, let t and s be unknowns
        // Intersection = Start + StartToEnd * t = rayOrigin + rayDirection * s
        // Rearranged: StartToEnd * t - rayDirection * s = rayOrigin - Start
        // Cross product with rayDirection and StartToEnd respectively yields:
        // t = (rayOrigin - Start) × rayDirection / (StartToEnd × rayDirection)
        // s = (rayOrigin - Start) × StartToEnd / (StartToEnd × rayDirection)
        // t ∈ [0,1] means intersection is on segment, s ≥ 0 means intersection is on ray
        Vector2 startToOrigin = rayOrigin - Start;
        float s = startToOrigin.CrossProduct(StartToEnd) / denominator;
        if (s < 0.0f) { return false; }
        float t = startToOrigin.CrossProduct(rayDirection) / denominator;
        if (t < 0.0f || t > 1.0f) { return false; }
        intersection = Start + StartToEnd * t;
        return true;
    }

    /// <summary>
    /// Checks if the segment intersects with a ray.
    /// </summary>
    /// <param name="ray">The ray to check for intersection.</param>
    /// <returns>True if an intersection exists, false otherwise.</returns>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public bool IntersectWith(in Ray ray)
    {
        Vector2 intersection = default;
        return TryGetIntersection(ray, ref intersection);
    }

    /// <summary>
    /// Attempts to find the intersection point with another segment.
    /// </summary>
    /// <param name="other">The other segment to intersect with.</param>
    /// <param name="intersection">The intersection point if found.</param>
    /// <returns>True if an intersection exists, false otherwise.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetIntersection(in Segment other, ref Vector2 intersection)
    {
        ref readonly Vector2 otherStart = ref other.Start;
        ref readonly Vector2 otherStartToEnd = ref other.StartToEnd;

        float denominator = StartToEnd.CrossProduct(otherStartToEnd);

        if (Math.Abs(denominator) < 1e-4f) { return false; }

        Vector2 start1ToStart2 = otherStart - Start;
        float s = start1ToStart2.CrossProduct(StartToEnd) / denominator;
        if (s < 0.0f || s > 1.0f) { return false; }
        float t = start1ToStart2.CrossProduct(otherStartToEnd) / denominator;
        if (t < 0.0f || t > 1.0f) { return false; }
        intersection = Start + StartToEnd * t;
        return true;
    }

    /// <summary>
    /// Checks if the segment intersects with another segment.
    /// </summary>
    /// <param name="other">The other segment to check for intersection.</param>
    /// <returns>True if an intersection exists, false otherwise.</returns>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public bool IntersectWith(in Segment other)
    {
        Vector2 intersection = default;
        return TryGetIntersection(other, ref intersection);
    }

    /// <summary>
    /// Clips this segment by a shadow, returning the resulting clipped segments.
    /// </summary>
    /// <param name="shadow">The shadow used for clipping.</param>
    /// <param name="clips">Span to store the resulting clipped segments.</param>
    /// <returns>The number of clipped segments produced.</returns>
    public int ClipFrom(in Shadow shadow, Span<Segment> clips)
    {
        float scanDirection = shadow.RayScanDir;
        if (Math.Abs(scanDirection) < 1e-4f)
        {
            clips[0] = this;
            return 1;
        }

        int clipCount = 0;
        Vector2 intersection = default;
        ref readonly Segment occluder = ref shadow.Occluder;
        ref readonly Vector2 occluderStartToEnd = ref occluder.StartToEnd;
        ref readonly Ray ray1 = ref shadow.Ray1;
        ref readonly Ray ray2 = ref shadow.Ray2;

        if (TryGetIntersection(occluder, ref intersection))
        {
            float cross = StartToEnd.CrossProduct(occluderStartToEnd);
            if (Math.Abs(cross) >= 1e-4f)
            {
                Segment newClip = new(intersection, cross * scanDirection > 0.0f ? Start : End);
                if (newClip.StartToEnd != Vector2.Zero)
                {
                    clips[clipCount++] = newClip;
                }
            }
        }

        if (TryGetIntersection(ray1, ref intersection))
        {
            float cross = StartToEnd.CrossProduct(ray1.Direction);
            if (Math.Abs(cross) >= 1e-4f)
            {
                Segment newClip = new(intersection, cross * scanDirection < 0.0f ? Start : End);
                if (newClip.StartToEnd != Vector2.Zero)
                {
                    clips[clipCount++] = newClip;
                }
            }
        }

        if (TryGetIntersection(ray2, ref intersection))
        {
            float cross = StartToEnd.CrossProduct(ray2.Direction);
            if (Math.Abs(cross) >= 1e-4f)
            {
                Segment newClip = new(intersection, cross * scanDirection > 0.0f ? Start : End);
                if (newClip.StartToEnd != Vector2.Zero)
                {
                    clips[clipCount++] = newClip;
                }
            }
        }

        if (clipCount == 0)
        {
            Vector2 occluderToSegment = Start - occluder.Start;
            if (occluderToSegment.CrossProduct(occluderStartToEnd) * scanDirection < 0.0f
                || occluderToSegment.CrossProduct(ray1.Direction) * scanDirection > 0.0f
                || (Start - occluder.End).CrossProduct(ray2.Direction) * scanDirection < 0.0f)
            {
                clips[clipCount++] = this;
            }
        }

        return clipCount;
    }

    /// <summary>
    /// Clips this segment by a ray range, returning the resulting clipped segments.
    /// </summary>
    /// <param name="rayRange">The ray range used for clipping.</param>
    /// <param name="clips">Span to store the resulting clipped segments.</param>
    /// <returns>The number of clipped segments produced.</returns>
    public int ClipFrom(in RayRange rayRange, Span<Segment> clips)
    {
        float scanDirection = rayRange.RayScanDir;
        if (Math.Abs(scanDirection) < 1e-4f)
        {
            clips[0] = this;
            return 1;
        }

        int clipCount = 0;
        Vector2 intersection = default;
        ref readonly Ray startRay = ref rayRange.Start;
        ref readonly Ray endRay = ref rayRange.End;

        if (TryGetIntersection(startRay, ref intersection))
        {
            float cross = StartToEnd.CrossProduct(startRay.Direction);
            if (Math.Abs(cross) >= 1e-4f)
            {
                Segment newClip = new(intersection, cross * scanDirection < 0.0f ? Start : End);
                if (newClip.StartToEnd != Vector2.Zero)
                {
                    clips[clipCount++] = newClip;
                }
            }
        }

        if (TryGetIntersection(endRay, ref intersection))
        {
            float cross = StartToEnd.CrossProduct(endRay.Direction);
            if (Math.Abs(cross) >= 1e-4f)
            {
                Segment newClip = new(intersection, cross * scanDirection > 0.0f ? Start : End);
                if (newClip.StartToEnd != Vector2.Zero)
                {
                    clips[clipCount++] = newClip;
                }
            }
        }

        if (clipCount == 0)
        {
            Vector2 originToSegment = Start - rayRange.Origin;
            if (originToSegment.CrossProduct(startRay.Direction) * scanDirection > 0
                || originToSegment.CrossProduct(endRay.Direction) * scanDirection < 0)
            {
                clips[clipCount++] = this;
            }
        }

        return clipCount;
    }

    /// <summary>
    /// Checks if the segment intersects with a ray range.
    /// </summary>
    /// <param name="rayRange">The ray range to check for intersection.</param>
    /// <returns>True if an intersection exists, false otherwise.</returns>
    public bool IntersectWith(in RayRange rayRange)
    {
        Vector2 intersection = default;
        ref readonly Ray startRay = ref rayRange.Start;

        float scanDirection = rayRange.RayScanDir;
        if (Math.Abs(scanDirection) < 1e-4f)
        {
            return TryGetIntersection(startRay, ref intersection);
        }

        if (TryGetIntersection(startRay, ref intersection)) { return true; }

        ref readonly Ray endRay = ref rayRange.End;

        if (TryGetIntersection(endRay, ref intersection)) { return true; }

        Vector2 originToSegment = Start - rayRange.Origin;
        return originToSegment.CrossProduct(startRay.Direction) * scanDirection <= 0
            && originToSegment.CrossProduct(endRay.Direction) * scanDirection >= 0;
    }

    /// <summary>
    /// Calculates the squared distance from a point to this segment.
    /// </summary>
    /// <param name="point">The point to calculate the distance to.</param>
    /// <returns>The squared distance from the point to the segment.</returns>
    public float ToPointDistanceSquared(in Vector2 point)
    {
        if (Length == 0.0f)
        {
            return Vector2.DistanceSquared(Start, point);
        }

        Vector2 toPoint = point - Start;
        float projection = Vector2.Dot(toPoint, StartToEnd) / LengthSquared;

        if (projection >= 0.0f && projection <= 1.0f)
        {
            float cross = StartToEnd.CrossProduct(toPoint);
            return cross * cross / LengthSquared;
        }
        else if (projection < 0.0f)
        {
            return Vector2.DistanceSquared(Start, point);
        }
        else
        {
            return Vector2.DistanceSquared(End, point);
        }
    }

    public override string ToString()
    {
        return $"[Length: {Length:F2} | {Start} => {End}]";
    }

    public override int GetHashCode()
    {
        return _hashCode;
    }

    public override bool Equals(object? obj)
    {
        return obj is Segment other &&
               ((Start.Equals(other.Start) && End.Equals(other.End))
                || (Start.Equals(other.End) && End.Equals(other.Start)));
    }

    public static bool operator ==(in Segment left, in Segment right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(in Segment left, in Segment right)
    {
        return !(left == right);
    }
}

/// <summary>
/// Represents a 2D ray with an origin and direction.
/// </summary>
public struct Ray
{
    public Vector2 Origin;
    public Vector2 Direction;

    /// <summary>
    /// Initializes a new instance of the <see cref="Ray"/> struct.
    /// </summary>
    /// <param name="origin">The origin point of the ray.</param>
    /// <param name="direction">The direction vector of the ray (will be normalized).</param>
    public Ray(in Vector2 origin, in Vector2 direction)
    {
        Origin = origin;
        Direction = direction;
        NormalizeDirection();
    }

    /// <summary>
    /// Normalizes the direction vector of the ray.
    /// </summary>
    public void NormalizeDirection()
    {
        if (Direction != Vector2.Zero)
        {
            Direction.Normalize();
        }
    }
}

/// <summary>
/// Represents a range between two rays, useful for sector-based spatial queries.
/// </summary>
public class RayRange
{
    public Vector2 Origin;
    public Ray Start;
    public Ray End;
    public float RayScanDir;

    /// <summary>
    /// Initializes a new instance of the <see cref="RayRange"/> class.
    /// </summary>
    /// <param name="origin">The common origin point for both rays.</param>
    /// <param name="startDirection">The direction vector for the start ray.</param>
    /// <param name="endDirection">The direction vector for the end ray.</param>
    public RayRange(in Vector2 origin, in Vector2 startDirection, in Vector2 endDirection)
    {
        Origin = origin;
        Start = new(origin, startDirection);
        End = new(origin, endDirection);
        CalculateProperties();
    }

    /// <summary>
    /// Calculates the properties of the ray range.
    /// </summary>
    public void CalculateProperties()
    {
        RayScanDir = Start.Direction.CrossProduct(End.Direction);
    }

    /// <summary>
    /// Updates the origin of the ray range.
    /// </summary>
    /// <param name="newOrigin">The new origin point.</param>
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

/// <summary>
/// Represents a shadow cast by a convex hull from a light source.
/// </summary>
public struct Shadow
{
    public readonly ConvexHull ConvexHull;
    public Vector2 LightSource;
    public Segment Occluder;
    public Ray Ray1;
    public Ray Ray2;
    public float RayScanDir;
    public float DistanceToView;
    public Quadrant OccluderQuadrants;
    private int _hashCode;

    /// <summary>
    /// Initializes a new instance of the <see cref="Shadow"/> struct.
    /// </summary>
    /// <param name="convexHull">The convex hull casting the shadow.</param>
    /// <param name="lightSource">The position of the light source.</param>
    /// <param name="vertex1">The first vertex of the occluder edge.</param>
    /// <param name="vertex2">The second vertex of the occluder edge.</param>
    public Shadow(ConvexHull convexHull, Vector2 lightSource, Vector2 vertex1, Vector2 vertex2)
    {
        ConvexHull = convexHull;
        LightSource = lightSource;
        Occluder = new(vertex1, vertex2);
        Ray1 = new(vertex1, vertex1 - lightSource);
        Ray2 = new(vertex2, vertex2 - lightSource);
        CalculateProperties();
    }

    /// <summary>
    /// Calculates the properties of the shadow.
    /// </summary>
    public void CalculateProperties()
    {
        RayScanDir = Ray1.Direction.CrossProduct(Ray2.Direction);
        _hashCode = HashCode.Combine(LightSource, Occluder);
    }

    /// <summary>
    /// Recalculates the shadow with new parameters.
    /// </summary>
    /// <param name="lightSource">The new light source position.</param>
    /// <param name="vertex1">The new first vertex of the occluder edge.</param>
    /// <param name="vertex2">The new second vertex of the occluder edge.</param>
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

    public override int GetHashCode()
    {
        return _hashCode;
    }

    public override bool Equals(object? obj)
    {
        return obj is Shadow other &&
               LightSource.Equals(other.LightSource) &&
               Occluder.Equals(other.Occluder);
    }

    public static bool operator ==(in Shadow left, in Shadow right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(in Shadow left, in Shadow right)
    {
        return !(left == right);
    }
}