using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using ConvexHull = Barotrauma.Lights.ConvexHull;

namespace Whosyouradddy.ShadowCulling.Geometry
{
    public struct Segment
    {
        public Vector2 Start;
        public Vector2 End;
        public Vector2 StartToEnd;
        public Vector2 Center;
        public float LengthSquared;
        public float Length;

        public Segment(in Vector2 start, in Vector2 end)
        {
            Start = start;
            End = end;
            StartToEnd = default;
            Center = default;
            LengthSquared = default;
            Length = default;
            DoCaculate();
        }

        public void DoCaculate()
        {
            StartToEnd = End - Start;
            Center.X = (Start.X + End.X) / 2;
            Center.Y = (Start.Y + End.Y) / 2;
            LengthSquared = StartToEnd.LengthSquared();
            Length = (float)Math.Sqrt(LengthSquared);
        }

        public bool TryGetIntersection(in Ray ray, ref Vector2 intersection)
        {
            ref readonly Vector2 rayOrigin = ref ray.Origin;
            ref readonly Vector2 rayDirection = ref ray.Direction;

            float denominator = StartToEnd.CrossProduct(rayDirection);

            // segment和ray共线
            if (Math.Abs(denominator) < 1e-4f) { return false; }

            // 假设segment和ray存在交点，设未知数t、s
            // 交点 = Start + StartToEnd * t = rayOrigin + rayDirection * s
            // 移项得 StartToEnd * t - rayDirection * s = rayOrigin - Start
            // 对等式两边分别做rayDirection、StartToEnd的叉积可得
            // t = (rayOrigin - Start) × rayDirection / (StartToEnd × rayDirection)
            // s = (rayOrigin - Start) × StartToEnd / (StartToEnd × rayDirection)
            // t ∈ [0,1]时，交点在segment上，s ≥ 0时，交点在ray上
            Vector2 startToOrigin = rayOrigin - Start;
            float s = startToOrigin.CrossProduct(StartToEnd) / denominator;
            if (s < 0.0f) { return false; }
            float t = startToOrigin.CrossProduct(rayDirection) / denominator;
            if (t < 0.0f || t > 1.0f) { return false; }
            intersection = Start + StartToEnd * t;
            return true;
        }

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

        public int ClipFrom(in Shadow shadow, Span<Segment> clips)
        {
            float dir = shadow.RayScanDir;
            if (Math.Abs(dir) < 1e-4f)
            {
                clips[0] = this;
                return 1;
            }

            int count = 0;
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
                    Segment newClip = new(intersection, cross * dir > 0.0f ? Start : End);
                    if (newClip.StartToEnd != Vector2.Zero)
                    {
                        clips[count++] = newClip;
                    }
                }
            }

            if (TryGetIntersection(ray1, ref intersection))
            {
                float cross = StartToEnd.CrossProduct(ray1.Direction);
                if (Math.Abs(cross) >= 1e-4f)
                {
                    Segment newClip = new Segment(intersection, cross * dir < 0.0f ? Start : End);
                    if (newClip.StartToEnd != Vector2.Zero)
                    {
                        clips[count++] = newClip;
                    }
                }
            }

            if (TryGetIntersection(ray2, ref intersection))
            {
                float cross = StartToEnd.CrossProduct(ray2.Direction);
                if (Math.Abs(cross) >= 1e-4f)
                {
                    Segment newClip = new(intersection, cross * dir > 0.0f ? Start : End);
                    if (newClip.StartToEnd != Vector2.Zero)
                    {
                        clips[count++] = newClip;
                    }
                }
            }

            if (count == 0)
            {
                Vector2 occluderToSegment = Start - occluder.Start;
                if (occluderToSegment.CrossProduct(occluderStartToEnd) * dir < 0.0f
                    || occluderToSegment.CrossProduct(ray1.Direction) * dir > 0.0f
                    || (Start - occluder.End).CrossProduct(ray2.Direction) * dir < 0.0f)
                {
                    clips[count++] = this;
                }
            }

            return count;
        }

        public int ClipFrom(in Shadow shadow, out Segment[] clips)
        {
            Span<Segment> span = stackalloc Segment[3];
            int count = ClipFrom(shadow, span);
            clips = span.Slice(0, count).ToArray();
            return count;
        }

        public int ClipFrom(IEnumerable<Shadow> shadows, out LinkedList<Segment> clips)
        {
            clips = new();
            clips.AddLast(this);

            foreach (var shadow in shadows)
            {
                LinkedListNode<Segment>? node = clips.First;
                if (node == null) { break; }
                do
                {
                    var nextNode = node.Next;
                    ref readonly Segment _segment = ref node.ValueRef;
                    int length = _segment.ClipFrom(shadow, out Segment[] _segments);
                    for (int i = 0; i < length; i++)
                    {
                        clips.AddBefore(node, _segments[i]);

                    }
                    clips.Remove(node);
                    node = nextNode;
                } while (node != null);
            }

            return clips.Count;
        }

        public int ClipFrom(in RayRange rayRange, Span<Segment> clips)
        {
            float dir = rayRange.RayScanDir;
            if (Math.Abs(dir) < 1e-4f)
            {
                clips[0] = this;
                return 1;
            }

            int count = 0;
            Vector2 intersection = default;
            ref readonly Ray start = ref rayRange.Start;
            ref readonly Ray end = ref rayRange.End;

            if (TryGetIntersection(start, ref intersection))
            {
                float cross = StartToEnd.CrossProduct(start.Direction);
                if (Math.Abs(cross) >= 1e-4f)
                {
                    Segment newClip = new(intersection, cross * dir < 0.0f ? Start : End);
                    if (newClip.StartToEnd != Vector2.Zero)
                    {
                        clips[count++] = newClip;
                    }
                }
            }

            if (TryGetIntersection(end, ref intersection))
            {
                float cross = StartToEnd.CrossProduct(end.Direction);
                if (Math.Abs(cross) >= 1e-4f)
                {
                    Segment newClip = new(intersection, cross * dir > 0.0f ? Start : End);
                    if (newClip.StartToEnd != Vector2.Zero)
                    {
                        clips[count++] = newClip;
                    }
                }
            }

            if (count == 0)
            {
                Vector2 originToSegment = Start - rayRange.Origin;
                if (originToSegment.CrossProduct(start.Direction) * dir > 0
                    || originToSegment.CrossProduct(end.Direction) * dir < 0)
                {
                    clips[count++] = this;
                }
            }

            return count;
        }

        public int ClipFrom(in RayRange rayRange, out Segment[] clips)
        {
            Span<Segment> tempSpan = stackalloc Segment[2];
            int count = ClipFrom(rayRange, tempSpan);
            clips = tempSpan.Slice(0, count).ToArray();
            return count;
        }

        public bool IntersectWith(in RayRange rayRange)
        {
            Vector2 intersection = default;
            ref readonly Ray start = ref rayRange.Start;

            float dir = rayRange.RayScanDir;
            if (Math.Abs(dir) < 1e-4f)
            {
                return TryGetIntersection(start, ref intersection);
            }

            if (TryGetIntersection(start, ref intersection)) { return true; }

            ref readonly Ray end = ref rayRange.End;

            if (TryGetIntersection(end, ref intersection)) { return true; }

            Vector2 originToSegment = Start - rayRange.Origin;
            return originToSegment.CrossProduct(start.Direction) * dir <= 0
                && originToSegment.CrossProduct(end.Direction) * dir >= 0;
        }


        public override string ToString()
        {
            return $"[Length: {StartToEnd.Length()} | {Start} => {End}]";
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Length);
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

    public struct Ray
    {
        public Vector2 Origin;
        public Vector2 Direction;

        public Ray(in Vector2 origin, in Vector2 direction)
        {
            Origin = origin;
            Direction = direction;
            DoCaculate();
        }

        public void DoCaculate()
        {
            if (Direction != Vector2.Zero)
            {
                Direction.Normalize();
            }
        }
    }

    public class RayRange
    {
        public Vector2 Origin;
        public Ray Start;
        public Ray End;
        public float RayScanDir;

        public RayRange(in Vector2 origin, in Vector2 start, in Vector2 end)
        {
            Origin = origin;
            Start = new(origin, start);
            End = new(origin, end);
            RayScanDir = default;
            DoCaculate();
        }

        public void DoCaculate()
        {
            RayScanDir = Start.Direction.CrossProduct(End.Direction);
        }

        public void DoCaculate(in Vector2 origin)
        {
            Origin.X = origin.X;
            Origin.Y = origin.Y;
            Start.Origin.X = origin.X;
            Start.Origin.Y = origin.Y;
            End.Origin.X = origin.X;
            End.Origin.Y = origin.Y;
        }
    }

    public struct Shadow
    {
        public Vector2 LightSource;
        public Segment Occluder;
        public Ray Ray1;
        public Ray Ray2;
        public float RayScanDir;

        public Shadow(Vector2 lightSource, Vector2 vertex1, Vector2 vertex2)
        {
            LightSource = lightSource;
            Occluder = new(vertex1, vertex2);
            Ray1 = new(vertex1, vertex1 - lightSource);
            Ray2 = new(vertex2, vertex2 - lightSource);
            RayScanDir = default;
            DoCaculate();
        }

        public void DoCaculate()
        {
            RayScanDir = Ray1.Direction.CrossProduct(Ray2.Direction);
        }

        public void DoCaculate(in Vector2 lightSource, in Vector2 vertex1, in Vector2 vertex2)
        {
            LightSource.X = lightSource.X;
            LightSource.Y = lightSource.Y;

            Occluder.Start.X = vertex1.X;
            Occluder.Start.Y = vertex1.Y;
            Occluder.End.X = vertex2.X;
            Occluder.End.Y = vertex2.Y;
            Occluder.DoCaculate();

            Ray1.Origin.X = vertex1.X;
            Ray1.Origin.Y = vertex1.Y;
            Ray1.Direction.X = vertex1.X - lightSource.X;
            Ray1.Direction.Y = vertex1.Y - lightSource.Y;
            Ray1.DoCaculate();

            Ray2.Origin.X = vertex2.X;
            Ray2.Origin.Y = vertex2.Y;
            Ray2.Direction.X = vertex2.X - lightSource.X;
            Ray2.Direction.Y = vertex2.Y - lightSource.Y;
            Ray2.DoCaculate();

            DoCaculate();
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(LightSource, Occluder);
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

    public static class Extensions
    {
        public static float CrossProduct(in this Vector2 a, in Vector2 b)
        {
            return a.X * b.Y - a.Y * b.X;
        }
    }
}