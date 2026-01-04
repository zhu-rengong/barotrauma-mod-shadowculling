using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Barotrauma;
using Barotrauma.Extensions;
using Barotrauma.Items.Components;

using ConvexHull = Barotrauma.Lights.ConvexHull;
using LightManager = Barotrauma.Lights.LightManager;
using ConvexHullList = Barotrauma.Lights.ConvexHullList;

using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Whosyouradddy.ShadowCulling.Geometry;


namespace Whosyouradddy.ShadowCulling
{
    public partial class Plugin : IAssemblyPlugin
    {
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

        private const int HULLS_PER_CULLING = 15;
        private const int ENTITIES_PER_CULLING = 65;
        private const double CULLING_INTERVAL = 0.05;
        private const float SHADOW_PREDICTION_TOLERANCE_MULTIPLIER = 1000.0f;
        private static double lastCullingUpdateTime;
        private static double lastPerformanceLogTime;
        private static bool cullingStateDirty = false;

        private static Shadow[] validShadowBuffer = new Shadow[1024];
        private static LinkedList<int> shadowIndexLinkedList = new();
        private static Dictionary<Quadrant, RayRange> quadrants = new(4);
        private static Dictionary<int, Quadrant> shadowIndexQuadrant = new(1024);
        private static List<int> sortedShadowIndices = new(1024);
        private static LinkedList<Segment> shadowClippingOccluders = new();
        private static HashSet<int> predictableOccluderStart = new(1024);
        private static HashSet<int> predictableOccluderEnd = new(1024);
        private static List<Hull> hullsForCulling = new(1024);
        private static List<MapEntity> entitiesForCulling = new(8192);
        private static List<Character> charactersForCulling = new(256);
        private const int PARALLELISM = 4;
        private const int CONCURRENCY_LEVEL = PARALLELISM * 3;
        private static ConcurrentDictionary<object, RectangleF> entityVisibleExtents = new(CONCURRENCY_LEVEL, 32768);
        private static ConcurrentDictionary<object, Hull?> entityHull = new(CONCURRENCY_LEVEL, 8192);
        private static ConcurrentDictionary<object, bool> isEntityCulled = new(CONCURRENCY_LEVEL, 8192);
        private static Vector2? previousViewRelativePos;
        private static ObjectPool<LinkedList<Segment>> poolLinkedListSegment = new(() => new());

        private static ParallelOptions cullingParallelOptions = new() { MaxDegreeOfParallelism = PARALLELISM };
        private static Stopwatch cullingPerformanceTimer = new();

        public static bool DisallowCulling =>
            Screen.Selected is { IsEditor: true }
            || !GameMain.LightManager.LosEnabled
            || GameMain.LightManager.LosMode == LosMode.None
            || (GameMain.IsSingleplayer
                ? GameMain.GameSession == null || !GameMain.GameSession.IsRunning
                : !GameMain.Client?.GameStarted ?? true);

        private static void TryClearAll()
        {
            if (cullingStateDirty)
            {
                Array.Clear(validShadowBuffer);
                hullsForCulling.Clear();
                entitiesForCulling.Clear();
                charactersForCulling.Clear();
                entityVisibleExtents.Clear();
                entityHull.Clear();
                isEntityCulled.Clear();
                previousViewRelativePos = null;
                LuaCsLogger.LogMessage($"Mod:ShadowCulling | Clear All!");
                cullingStateDirty = false;
            }
        }

        public partial void InitializeProjSpecific()
        {
            quadrants.Add(Quadrant.RightTop, new(Vector2.Zero, Vector2.UnitX, Vector2.UnitY));
            quadrants.Add(Quadrant.LeftTop, new(Vector2.Zero, -Vector2.UnitX, Vector2.UnitY));
            quadrants.Add(Quadrant.LeftBottom, new(Vector2.Zero, -Vector2.UnitX, -Vector2.UnitY));
            quadrants.Add(Quadrant.RightBottom, new(Vector2.Zero, Vector2.UnitX, -Vector2.UnitY));
        }

        public static void PerformEntityCulling(bool debug = false)
        {
            if (!debug && !CullingEnabled) { return; }

            if (DisallowCulling
                || LightManager.ViewTarget is not Entity viewTarget
                || Screen.Selected?.Cam is not Camera camera)
            {
                TryClearAll();
                return;
            }

            if (lastCullingUpdateTime > Timing.TotalTime - CULLING_INTERVAL) { return; }

            Vector2 viewTargetPosition = viewTarget is Character viewTargetCharacter &&
                viewTargetCharacter.AnimController?.GetLimb(LimbType.Head) is Limb head &&
                !head.IsSevered && !head.Removed
                    ? head.body.DrawPosition
                    : viewTarget.DrawPosition;
            Vector2 viewRelativePosition = viewTargetPosition;
            if (viewTarget.Submarine != null)
            {
                viewRelativePosition -= viewTarget.Submarine.DrawPosition;
            }
            if (!previousViewRelativePos.HasValue || (viewRelativePosition - previousViewRelativePos.Value).LengthSquared() > 1e6f) { previousViewRelativePos = viewRelativePosition; }
            Vector2 viewPosInterpolation;
            viewPosInterpolation.X = viewRelativePosition.X * 0.1f + previousViewRelativePos.Value.X * 0.9f;
            viewPosInterpolation.Y = viewRelativePosition.Y * 0.1f + previousViewRelativePos.Value.Y * 0.9f;
            Vector2 viewDirection = viewPosInterpolation - previousViewRelativePos.Value;
            previousViewRelativePos = viewPosInterpolation;

            foreach (RayRange quadrant in quadrants.Values)
            {
                quadrant.DoCalculate(viewTargetPosition);
            }

            cullingPerformanceTimer.Restart();

            int validShadowNumber = 0;
            Span<Segment> occluderClipBuffer = stackalloc Segment[3];

            shadowIndexLinkedList.Clear();
            shadowIndexQuadrant.Clear();

            Rectangle cameraViewBounds = camera.WorldView;

            // Get the convex hulls that intersect with the camera viewport
            foreach (ConvexHullList hullList in ConvexHull.HullLists)
            {
                foreach (ConvexHull convexHull in hullList.List)
                {
                    if (convexHull.IsInvalid || !convexHull.Enabled || convexHull.ShadowVertexCount < 6) { continue; }

                    Rectangle convexHullAABB = convexHull.BoundingBox;
                    // In the world coordinate, the origin of the ConvexHull.BoundingBox is assumed to be left-bottom corner(perhaps due to historical reasons?)
                    // Here, it is necessary to convert its origin to the top-left corner to maintain consistency with other world rectangles.
                    convexHullAABB.Y += convexHullAABB.Height;

                    Vector2 offsetToAbs = Vector2.Zero;
                    if (convexHull.ParentEntity?.Submarine is Submarine parentSubmarine)
                    {
                        offsetToAbs.X = parentSubmarine.DrawPosition.X;
                        offsetToAbs.Y = parentSubmarine.DrawPosition.Y;
                        convexHullAABB.X += (int)offsetToAbs.X;
                        convexHullAABB.Y += (int)offsetToAbs.Y;
                    }

                    if (convexHullAABB.X > cameraViewBounds.X + cameraViewBounds.Width) { continue; }
                    if (convexHullAABB.X + convexHullAABB.Width < cameraViewBounds.X) { continue; }
                    if (convexHullAABB.Y < cameraViewBounds.Y - cameraViewBounds.Height) { continue; }
                    if (convexHullAABB.Y - convexHullAABB.Height > cameraViewBounds.Y) { continue; }

                    Vector2 vertexPos0 = convexHull.losVertices[0].Pos + convexHull.losOffsets[0] + offsetToAbs;
                    Vector2 vertexPos1 = convexHull.losVertices[1].Pos + convexHull.losOffsets[1] + offsetToAbs;
                    if (Vector2.DistanceSquared(vertexPos0, vertexPos1) < 1.0f) { continue; }

                    Vector2 occluderVertexUnitOffset = Vector2.Normalize(vertexPos1 - vertexPos0);

                    if (validShadowNumber >= validShadowBuffer.Length)
                    {
                        Array.Resize(ref validShadowBuffer, validShadowBuffer.Length + 1024);
                    }

                    validShadowBuffer[validShadowNumber] = new(
                        convexHull,
                        lightSource: viewTargetPosition,
                        vertex1: vertexPos0 - occluderVertexUnitOffset,
                        vertex2: vertexPos1 + occluderVertexUnitOffset
                    );

                    ref Shadow shadow = ref validShadowBuffer[validShadowNumber];
                    ref Segment occluder = ref shadow.Occluder;

                    if (convexHull.ParentEntity is Item item
                        && item.GetComponent<Door>() is Door { OpenState: > 0.0f and < 1.0f } door)
                    {
                        float doorStateDelta = (door.IsOpen ? door.OpeningSpeed : door.ClosingSpeed) * (float)Timing.Step;
                        Vector2 doorLosVertexOffset = 0.5f * occluderVertexUnitOffset
                                                    * MathF.Min(
                                                        MathF.Max(0.0f, occluder.Length - doorStateDelta),
                                                        800.0f * doorStateDelta);

                        occluder.End -= doorLosVertexOffset;
                        occluder.Start += doorLosVertexOffset;
                        shadow.DoCalculate(viewTargetPosition, occluder.Start, occluder.End);
                    }

                    Quadrant occluderQuadrant = Quadrant.None;
                    foreach (var (quadrant, rayRange) in quadrants)
                    {
                        if (occluder.IntersectWith(rayRange))
                        {
                            occluderQuadrant |= quadrant;
                        }
                    }

                    shadowIndexLinkedList.AddLast(validShadowNumber);
                    shadowIndexQuadrant.Add(validShadowNumber, occluderQuadrant);

                    validShadowNumber++;
                }
            }

            // Exclude convex hulls that are in shadow
            // When there are enough convex hulls,
            // sort them by their distance to the view target,
            // and use nearer hulls to prioritize determining whether farther ones are in shadow.
            // This can significantly improve the hit rate of predicate.
            shadowIndexLinkedList = new(shadowIndexLinkedList.OrderBy(
                shadowIndex => (viewTargetPosition - validShadowBuffer[shadowIndex].Occluder.Center).LengthSquared()
            ));

            LinkedListNode<int>? currentShadowNode = shadowIndexLinkedList.Last;
            while (currentShadowNode != null)
            {
                var previousShadowNode = currentShadowNode.Previous;
                var nextShadowNode = currentShadowNode.Next;
                int currentShadowIndex = currentShadowNode.Value;
                ref readonly Shadow currentShadow = ref validShadowBuffer[currentShadowIndex];
                ref readonly Segment entireOccluder = ref currentShadow.Occluder;
                Quadrant quadrant = shadowIndexQuadrant[currentShadowIndex];
                shadowClippingOccluders.AddLast(entireOccluder);

                shadowIndexLinkedList.Remove(currentShadowNode);
                foreach (int otherShadowIndex in shadowIndexLinkedList)
                {
                    if (!quadrant.HasAnyFlag(shadowIndexQuadrant[otherShadowIndex])) { continue; }
                    LinkedListNode<Segment>? clipNode = shadowClippingOccluders.First;
                    if (clipNode == null) { break; }
                    ref readonly Shadow otherShadow = ref validShadowBuffer[otherShadowIndex];
                    do
                    {
                        var nextClipNode = clipNode.Next;
                        ref readonly Segment occluder = ref clipNode.ValueRef;
                        int clipCount = occluder.ClipFrom(otherShadow, occluderClipBuffer);
                        if (clipCount != 1 || occluder != occluderClipBuffer[0])
                        {
                            for (int clipIndex = 0; clipIndex < clipCount; clipIndex++)
                            {
                                shadowClippingOccluders.AddBefore(clipNode, occluderClipBuffer[clipIndex]);
                            }
                            shadowClippingOccluders.Remove(clipNode);
                        }
                        clipNode = nextClipNode;
                    } while (clipNode != null);
                }

                if (shadowClippingOccluders.Count > 0)
                {
                    if (previousShadowNode != null)
                    {
                        shadowIndexLinkedList.AddAfter(previousShadowNode, currentShadowNode);
                    }
                    else if (nextShadowNode != null)
                    {
                        shadowIndexLinkedList.AddBefore(nextShadowNode, currentShadowNode);
                    }
                    else
                    {
                        shadowIndexLinkedList.AddLast(currentShadowNode);
                    }
                }

                shadowClippingOccluders.Clear();

                currentShadowNode = previousShadowNode;
            }

            sortedShadowIndices.Clear();
            sortedShadowIndices.AddRange(shadowIndexLinkedList);

            // Calculate the shadow tolerance based on the view's predicted position to avoid "face-close loading".
            if (viewDirection.LengthSquared() > 0.01f)
            {
                predictableOccluderStart.Clear();
                predictableOccluderEnd.Clear();

                Vector2 predictedPosition = viewTargetPosition + viewDirection;
                foreach (int currentShadowIndex in sortedShadowIndices)
                {
                    ref Segment currentOccluder = ref validShadowBuffer[currentShadowIndex].Occluder;
                    Vector2 startToView = viewTargetPosition - currentOccluder.Start;
                    if (startToView.CrossProduct(currentOccluder.StartToEnd)
                        * startToView.CrossProduct(viewDirection) < 0.0f)
                    {
                        predictableOccluderStart.Add(currentShadowIndex);
                    }
                    Vector2 endToView = viewTargetPosition - currentOccluder.End;
                    if (endToView.CrossProduct(currentOccluder.StartToEnd)
                        * endToView.CrossProduct(viewDirection) > 0.0f)
                    {
                        predictableOccluderEnd.Add(currentShadowIndex);
                    }
                }

                foreach (int currentShadowIndex in sortedShadowIndices)
                {
                    ref Shadow currentShadow = ref validShadowBuffer[currentShadowIndex];
                    ref Segment currentOccluder = ref currentShadow.Occluder;

                    if (predictableOccluderStart.Contains(currentShadowIndex))
                    {
                        foreach (int otherShadowIndex in sortedShadowIndices)
                        {
                            if (currentShadowIndex != otherShadowIndex)
                            {
                                ref readonly Shadow otherShadow = ref validShadowBuffer[otherShadowIndex];
                                ref readonly Segment otherOccluder = ref otherShadow.Occluder;
                                if (otherOccluder.ToPointDistanceSquared(currentOccluder.Start) < 100.0f)
                                {
                                    bool isOtherStartCloseEnough = (otherOccluder.Start - currentOccluder.Start).LengthSquared() < 100.0f;
                                    bool isOtherEndCloseEnough = (otherOccluder.End - currentOccluder.Start).LengthSquared() < 100.0f;

                                    if ((!isOtherStartCloseEnough && !isOtherEndCloseEnough)
                                        || (isOtherStartCloseEnough && !predictableOccluderStart.Contains(otherShadowIndex))
                                        || (isOtherEndCloseEnough && !predictableOccluderEnd.Contains(otherShadowIndex)))
                                    {
                                        goto SKIP_PREDICATION;
                                    }
                                }
                            }
                        }

                        float predictionOffset = MathF.Min(
                            MathF.Abs((viewTargetPosition - currentOccluder.Start).VectorAngle(predictedPosition - currentOccluder.Start)) * SHADOW_PREDICTION_TOLERANCE_MULTIPLIER,
                            currentOccluder.Length - 1.0f);
                        currentOccluder.Start += Vector2.Normalize(currentOccluder.StartToEnd) * predictionOffset;
                        currentShadow.DoCalculate(viewTargetPosition, currentOccluder.Start, currentOccluder.End);
                    SKIP_PREDICATION:;
                    }

                    if (predictableOccluderEnd.Contains(currentShadowIndex))
                    {
                        foreach (int otherShadowIndex in sortedShadowIndices)
                        {
                            if (currentShadowIndex != otherShadowIndex)
                            {
                                ref readonly Shadow otherShadow = ref validShadowBuffer[otherShadowIndex];
                                ref readonly Segment otherOccluder = ref otherShadow.Occluder;
                                if (otherOccluder.ToPointDistanceSquared(currentOccluder.End) < 100.0f)
                                {
                                    bool isOtherStartCloseEnough = (otherOccluder.Start - currentOccluder.End).LengthSquared() < 100.0f;
                                    bool isOtherEndCloseEnough = (otherOccluder.End - currentOccluder.End).LengthSquared() < 100.0f;

                                    if ((!isOtherStartCloseEnough && !isOtherEndCloseEnough)
                                        || (isOtherStartCloseEnough && !predictableOccluderStart.Contains(otherShadowIndex))
                                        || (isOtherEndCloseEnough && !predictableOccluderEnd.Contains(otherShadowIndex)))
                                    {
                                        goto SKIP_PREDICATION;
                                    }
                                }
                            }
                        }

                        float predictionOffset = MathF.Min(
                            MathF.Abs((viewTargetPosition - currentOccluder.End).VectorAngle(predictedPosition - currentOccluder.End)) * SHADOW_PREDICTION_TOLERANCE_MULTIPLIER,
                            currentOccluder.Length - 1.0f);
                        currentOccluder.End += Vector2.Normalize(-currentOccluder.StartToEnd) * predictionOffset;
                        currentShadow.DoCalculate(viewTargetPosition, currentOccluder.Start, currentOccluder.End);
                    SKIP_PREDICATION:;
                    }
                }
            }

            int totalCulled = 0;
            isEntityCulled.Clear();

            hullsForCulling.Clear();
            foreach (Hull hull in Hull.HullList)
            {
                if (hull.Submarine is Submarine sub && Submarine.visibleSubs.Contains(sub)
                    && hull.Volume > 40000.0f
                    && Submarine.RectsOverlap(hull.WorldRect, cameraViewBounds))
                {
                    hullsForCulling.Add(hull);
                }
            }

            if (hullsForCulling.Count > HULLS_PER_CULLING)
            {
                Parallel.For(
                    fromInclusive: 0,
                    toExclusive: (hullsForCulling.Count + HULLS_PER_CULLING - 1) / HULLS_PER_CULLING,
                    parallelOptions: cullingParallelOptions,
                    body: index =>
                    {
                        int startIndex = index * HULLS_PER_CULLING;
                        Cull(hullsForCulling,
                            fromInclusive: startIndex,
                            toExclusive: Math.Min(startIndex + HULLS_PER_CULLING, hullsForCulling.Count));
                    }
                );
            }
            else
            {
                Cull(hullsForCulling, fromInclusive: 0, toExclusive: hullsForCulling.Count);
            }

            entitiesForCulling.Clear();
            entitiesForCulling.AddRange(Submarine.visibleEntities);

            if (entitiesForCulling.Count > ENTITIES_PER_CULLING)
            {
                Parallel.For(
                    fromInclusive: 0,
                    toExclusive: (entitiesForCulling.Count + ENTITIES_PER_CULLING - 1) / ENTITIES_PER_CULLING,
                    parallelOptions: cullingParallelOptions,
                    body: index =>
                    {
                        int startIndex = index * ENTITIES_PER_CULLING;
                        Cull(entitiesForCulling,
                            fromInclusive: startIndex,
                            toExclusive: Math.Min(startIndex + ENTITIES_PER_CULLING, entitiesForCulling.Count));
                    }
                );
            }
            else
            {
                Cull(entitiesForCulling, fromInclusive: 0, toExclusive: entitiesForCulling.Count);
            }

            charactersForCulling.Clear();
            charactersForCulling.AddRange(Character.CharacterList.Where(c => c.IsVisible));
            Cull(charactersForCulling, fromInclusive: 0, toExclusive: charactersForCulling.Count);

            void Cull<T>(List<T> entities, int fromInclusive, int toExclusive) where T : Entity
            {
                Span<Segment> entityEdges = stackalloc Segment[8];
                Span<Segment> edgeClipBuffer = stackalloc Segment[3];
                LinkedList<Segment> shadowClippingEdges = poolLinkedListSegment.Get();
                int entitiesCulled = 0;

                for (int entityIndex = fromInclusive; entityIndex < toExclusive; entityIndex++)
                {
                    T entity = entities[entityIndex];
                    // The origin is at the top left
                    RectangleF entityAABB;

                    if (typeof(T) == typeof(Hull) && entity is Hull hull)
                    {
                        entityAABB = hull.WorldRect;
                    }
                    else if (typeof(T) == typeof(Character) && entity is Character character && character != viewTarget)
                    {
                        entityAABB = AABB.CalculateDynamic(character);
                    }
                    else if (typeof(T) == typeof(MapEntity))
                    {
                        if (entity is Item item)
                        {
                            if (!item.cachedVisibleExtents.HasValue || item.IsHidden || !item.Visible || item.isWire)
                            {
                                continue;
                            }

                            entityAABB = item.cachedVisibleExtents.Value;
                            entityAABB.Width -= entityAABB.X;
                            entityAABB.Height -= entityAABB.Y;
                            entityAABB.Y += entityAABB.Height;
                            entityAABB.Offset(entity.DrawPosition);

                            if (item.CurrentHull is Hull itemHull && isEntityCulled.TryGetValue(itemHull, out bool _))
                            {
                                RectangleF hullAABB = itemHull.WorldRect;
                                if (entityAABB.X > hullAABB.X && entityAABB.Y < hullAABB.Y
                                    && entityAABB.X + entityAABB.Width < hullAABB.X + hullAABB.Width
                                    && entityAABB.Y - entityAABB.Height > hullAABB.Y - hullAABB.Height)
                                {
                                    goto CULL;
                                }
                            }
                        }
                        else if (entity is Structure structure)
                        {
                            if (structure.IsHidden)
                            {
                                continue;
                            }

                            if (!entityVisibleExtents.TryGetValue(entity, out RectangleF extents))
                            {
                                entityVisibleExtents.TryAdd(entity, extents = AABB.CalculateFixed(structure));
                            }

                            entityAABB = extents;
                            entityAABB.Offset(entity.DrawPosition);

                            if (!entityHull.TryGetValue(entity, out Hull? structureHull))
                            {
                                entityHull.TryAdd(entity, structureHull = Hull.FindHull(structure.WorldPosition));
                            }

                            if (structureHull != null && isEntityCulled.TryGetValue(structureHull, out bool _))
                            {
                                RectangleF hullAABB = structureHull.WorldRect;
                                if (entityAABB.X > hullAABB.X && entityAABB.Y < hullAABB.Y
                                    && entityAABB.X + entityAABB.Width < hullAABB.X + hullAABB.Width
                                    && entityAABB.Y - entityAABB.Height > hullAABB.Y - hullAABB.Height)
                                {
                                    goto CULL;
                                }
                            }
                        }
                        else
                        {
                            continue;
                        }
                    }
                    else
                    {
                        continue;
                    }

                    Vector2 leftTop = new(entityAABB.X, entityAABB.Y);
                    Vector2 rightTop = new(entityAABB.X + entityAABB.Width, entityAABB.Y);
                    Vector2 leftBottom = new(entityAABB.X, entityAABB.Y - entityAABB.Height);
                    Vector2 rightBottom = new(rightTop.X, leftBottom.Y);

                    entityEdges[4] = new(leftTop, rightTop);
                    entityEdges[5] = new(rightTop, rightBottom);
                    entityEdges[6] = new(rightBottom, leftBottom);
                    entityEdges[7] = new(leftBottom, leftTop);

                    Quadrant entityQuadrant = Quadrant.None;
                    int numCoveredQuadrants = 0;
                    foreach (var (quadrant, rayRange) in quadrants)
                    {
                        for (int edgeIndex = 4; edgeIndex < 8; edgeIndex++)
                        {
                            ref readonly Segment edge = ref entityEdges[edgeIndex];
                            if (edge.IntersectWith(rayRange))
                            {
                                if (++numCoveredQuadrants > 2)
                                {
                                    goto SKIP;
                                }
                                entityQuadrant |= quadrant;
                                break;
                            }
                        }
                    }

                    int edgeCount = 0;
                    switch (entityQuadrant)
                    {
                        case Quadrant.RightTop:
                            entityEdges[edgeCount++] = entityEdges[7];
                            entityEdges[edgeCount++] = entityEdges[6];
                            break;
                        case Quadrant.LeftTop:
                            entityEdges[edgeCount++] = entityEdges[5];
                            entityEdges[edgeCount++] = entityEdges[6];
                            break;
                        case Quadrant.LeftBottom:
                            entityEdges[edgeCount++] = entityEdges[4];
                            entityEdges[edgeCount++] = entityEdges[5];
                            break;
                        case Quadrant.RightBottom:
                            entityEdges[edgeCount++] = entityEdges[7];
                            entityEdges[edgeCount++] = entityEdges[4];
                            break;
                        case Quadrant.Top:
                            entityEdges[edgeCount++] = entityEdges[6];
                            break;
                        case Quadrant.Left:
                            entityEdges[edgeCount++] = entityEdges[5];
                            break;
                        case Quadrant.Bottom:
                            entityEdges[edgeCount++] = entityEdges[4];
                            break;
                        case Quadrant.Right:
                            entityEdges[edgeCount++] = entityEdges[7];
                            break;
                        default:
                            break;
                    }

                    for (int edgeIndex = 0; edgeIndex < edgeCount; edgeIndex++)
                    {
                        shadowClippingEdges.AddLast(entityEdges[edgeIndex]);

                        foreach (int shadowIndex in sortedShadowIndices)
                        {
                            if (!entityQuadrant.HasAnyFlag(shadowIndexQuadrant[shadowIndex])) { continue; }
                            LinkedListNode<Segment>? clipNode = shadowClippingEdges.First;
                            if (clipNode == null) { break; }
                            ref readonly Shadow shadow = ref validShadowBuffer[shadowIndex];
                            do
                            {
                                var nextClipNode = clipNode.Next;
                                ref readonly Segment edge = ref clipNode.ValueRef;
                                int clipCount = edge.ClipFrom(shadow, edgeClipBuffer);
                                if (clipCount != 1 || edge != edgeClipBuffer[0])
                                {
                                    for (int clipIndex = 0; clipIndex < clipCount; clipIndex++)
                                    {
                                        shadowClippingEdges.AddBefore(clipNode, edgeClipBuffer[clipIndex]);
                                    }
                                    shadowClippingEdges.Remove(clipNode);
                                }
                                clipNode = nextClipNode;
                            } while (clipNode != null);
                        }

                        bool refuseCulling = shadowClippingEdges.Count > 0;
                        shadowClippingEdges.Clear();
                        if (refuseCulling)
                        {
                            goto SKIP;
                        }
                    }

                CULL:
                    isEntityCulled.TryAdd(entity, true);
                    entitiesCulled++;
                SKIP:;
                }

                poolLinkedListSegment.Return(shadowClippingEdges);

                Interlocked.Add(ref totalCulled, entitiesCulled);
            }

            cullingPerformanceTimer.Stop();
            GameMain.PerformanceCounter.AddElapsedTicks("Mod:ShadowCulling", cullingPerformanceTimer.ElapsedTicks);

            if (DebugLog && Timing.TotalTime - lastPerformanceLogTime >= 2.0f)
            {
                float averageCullingTime = GameMain.PerformanceCounter.GetAverageElapsedMillisecs("Mod:ShadowCulling");
                LuaCsLogger.LogMessage($"Mod:ShadowCulling | Mean: {averageCullingTime} | Cull: {totalCulled}/{entitiesForCulling.Count} | Shadows: {sortedShadowIndices.Count}/{validShadowNumber} | Hulls: {hullsForCulling.Count}");
                lastPerformanceLogTime = Timing.TotalTime;
            }

            lastCullingUpdateTime = Timing.TotalTime;

            cullingStateDirty = true;
        }
    }
}