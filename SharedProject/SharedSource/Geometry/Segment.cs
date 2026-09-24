namespace ShadowCulling;

/// <summary>
/// Represents a 2D line segment with intersection and clipping capabilities. Segments are created in bulk by the
/// clipping loops and read through <c>ref readonly</c> handles, so non-mutating members are <c>readonly</c> and the
/// length and hash code are calculated on demand.
/// </summary>
public struct Segment : IEquatable<Segment>
{
    public Vector2 Start;
    public Vector2 End;
    public Vector2 StartToEnd;
    public Vector2 Center;
    public float LengthSquared;

    /// <summary>Initializes a new instance of the <see cref="Segment"/> struct.</summary>
    public Segment(in Vector2 start, in Vector2 end)
    {
        Start = start;
        End = end;
        CalculateProperties();
    }

    /// <summary>Gets the length of the segment, derived from <see cref="LengthSquared"/>.</summary>
    public readonly float Length => MathF.Sqrt(LengthSquared);

    /// <summary>Calculates the derived properties of the segment.</summary>
    public void CalculateProperties()
    {
        StartToEnd = End - Start;
        Center.X = (Start.X + End.X) * 0.5f;
        Center.Y = (Start.Y + End.Y) * 0.5f;
        LengthSquared = StartToEnd.LengthSquared();
    }

    /// <summary>Attempts to find the intersection point with a ray.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool TryGetIntersection(in Ray2D ray, ref Vector2 intersection, out float denominator)
    {
        ref readonly Vector2 rayOrigin = ref ray.Origin;
        ref readonly Vector2 rayDirection = ref ray.Direction;

        denominator = StartToEnd.CrossProduct(rayDirection);

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

    /// <summary>Checks if the segment intersects with a ray.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public readonly bool IntersectWith(in Ray2D ray)
    {
        Vector2 intersection = default;
        return TryGetIntersection(ray, ref intersection, out _);
    }

    /// <summary>Attempts to find the intersection point with another segment.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool TryGetIntersection(in Segment other, ref Vector2 intersection, out float denominator)
    {
        ref readonly Vector2 otherStart = ref other.Start;
        ref readonly Vector2 otherStartToEnd = ref other.StartToEnd;

        denominator = StartToEnd.CrossProduct(otherStartToEnd);

        if (Math.Abs(denominator) < 1e-4f) { return false; }

        Vector2 start1ToStart2 = otherStart - Start;
        float s = start1ToStart2.CrossProduct(StartToEnd) / denominator;
        if (s < 0.0f || s > 1.0f) { return false; }
        float t = start1ToStart2.CrossProduct(otherStartToEnd) / denominator;
        if (t < 0.0f || t > 1.0f) { return false; }
        intersection = Start + StartToEnd * t;
        return true;
    }

    /// <summary>Checks if the segment intersects with another segment.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public readonly bool IntersectWith(in Segment other)
    {
        Vector2 intersection = default;
        return TryGetIntersection(other, ref intersection, out _);
    }

    /// <summary>Clips this segment by a shadow, returning the resulting clipped segments.</summary>
    public readonly int ClipFrom(in Shadow shadow, Span<Segment> clips)
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
        ref readonly Ray2D ray1 = ref shadow.Ray1;
        ref readonly Ray2D ray2 = ref shadow.Ray2;

        // These crossings are never near zero - TryGetIntersection only returns true for a cross product of at
        // least 1e-4 - so only their sign is left to check.
        if (TryGetIntersection(occluder, ref intersection, out float crossWithOccluder))
        {
            Vector2 clipEnd = crossWithOccluder * scanDirection > 0.0f ? Start : End;
            if (clipEnd != intersection)
            {
                clips[clipCount++] = new Segment(intersection, clipEnd);
            }
        }

        if (TryGetIntersection(ray1, ref intersection, out float crossWithRay1))
        {
            Vector2 clipEnd = crossWithRay1 * scanDirection < 0.0f ? Start : End;
            if (clipEnd != intersection)
            {
                clips[clipCount++] = new Segment(intersection, clipEnd);
            }
        }

        if (TryGetIntersection(ray2, ref intersection, out float crossWithRay2))
        {
            Vector2 clipEnd = crossWithRay2 * scanDirection > 0.0f ? Start : End;
            if (clipEnd != intersection)
            {
                clips[clipCount++] = new Segment(intersection, clipEnd);
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

    /// <summary>Clips this segment by a ray range, returning the resulting clipped segments.</summary>
    public readonly int ClipFrom(in RayRange rayRange, Span<Segment> clips)
    {
        float scanDirection = rayRange.RayScanDir;
        if (Math.Abs(scanDirection) < 1e-4f)
        {
            clips[0] = this;
            return 1;
        }

        int clipCount = 0;
        Vector2 intersection = default;
        ref readonly Ray2D startRay = ref rayRange.Start;
        ref readonly Ray2D endRay = ref rayRange.End;

        if (TryGetIntersection(startRay, ref intersection, out float crossWithStartRay))
        {
            Vector2 clipEnd = crossWithStartRay * scanDirection < 0.0f ? Start : End;
            if (clipEnd != intersection)
            {
                clips[clipCount++] = new Segment(intersection, clipEnd);
            }
        }

        if (TryGetIntersection(endRay, ref intersection, out float crossWithEndRay))
        {
            Vector2 clipEnd = crossWithEndRay * scanDirection > 0.0f ? Start : End;
            if (clipEnd != intersection)
            {
                clips[clipCount++] = new Segment(intersection, clipEnd);
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

    /// <summary>Checks if the segment intersects with a ray range.</summary>
    public readonly bool IntersectWith(in RayRange rayRange)
    {
        Vector2 intersection = default;
        ref readonly Ray2D startRay = ref rayRange.Start;

        float scanDirection = rayRange.RayScanDir;
        if (Math.Abs(scanDirection) < 1e-4f)
        {
            return TryGetIntersection(startRay, ref intersection, out _);
        }

        if (TryGetIntersection(startRay, ref intersection, out _)) { return true; }

        ref readonly Ray2D endRay = ref rayRange.End;

        if (TryGetIntersection(endRay, ref intersection, out _)) { return true; }

        Vector2 originToSegment = Start - rayRange.Origin;
        return originToSegment.CrossProduct(startRay.Direction) * scanDirection <= 0
            && originToSegment.CrossProduct(endRay.Direction) * scanDirection >= 0;
    }

    /// <summary>Calculates the squared distance from a point to this segment.</summary>
    public readonly float ToPointDistanceSquared(in Vector2 point)
    {
        // Guards the divide by zero below, on LengthSquared so that it costs no square root.
        if (LengthSquared == 0.0f)
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

    public override readonly string ToString()
    {
        return $"[Length: {Length:F2} | {Start} => {End}]";
    }

    public override readonly int GetHashCode()
    {
        return HashCode.Combine(Start, End);
    }

    public override readonly bool Equals(object? obj) => obj is Segment other && this == other;

    public readonly bool Equals(Segment other) => (Start.Equals(other.Start) && End.Equals(other.End)) || (Start.Equals(other.End) && End.Equals(other.Start));

    public static bool operator ==(in Segment left, in Segment right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(in Segment left, in Segment right)
    {
        return !(left == right);
    }
}
