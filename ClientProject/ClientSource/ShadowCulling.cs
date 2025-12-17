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

        [HarmonyPatch(
             declaringType: typeof(Submarine),
             methodName: nameof(Submarine.CullEntities)
        )]
        class Submarine_CullEntities
        {
            static void Postfix()
            {
                if (CullingEnabled)
                {
                    PerformEntityCulling();
                }
            }
        }

        [HarmonyPatch(
             declaringType: typeof(Item),
             methodName: nameof(Item.Draw),
             argumentTypes: new Type[] { typeof(SpriteBatch), typeof(bool), typeof(bool) }
        )]
        class Item_Draw
        {
            static bool Prefix(Item __instance)
            {
                if (isEntityCulled.TryGetValue(__instance, out bool _))
                {
                    return false;
                }

                return true;
            }
        }

        [HarmonyPatch(
             declaringType: typeof(Structure),
             methodName: nameof(Structure.Draw),
             argumentTypes: new Type[] { typeof(SpriteBatch), typeof(bool), typeof(bool) }
        )]
        class Structure_Draw
        {
            static bool Prefix(Item __instance)
            {
                if (isEntityCulled.TryGetValue(__instance, out bool _))
                {
                    return false;
                }

                return true;
            }
        }

        private const int ENTITIES_PER_CULLING_BATCH = 65;
        private const double CULLING_UPDATE_INTERVAL_SECONDS = 0.05;
        private const float SHADOW_PREDICTION_TOLERANCE_MULTIPLIER = 5000.0f;
        private static double lastCullingUpdateTime;
        private static double lastPerformanceLogTime;
        private static bool cullingStateDirty = false;

        private static Shadow[] validShadowBuffer = new Shadow[1024];
        private static LinkedList<int> shadowIndexLinkedList = new();
        private static Dictionary<Quadrant, RayRange> quadrants = new(4);
        private static Dictionary<int, Quadrant> shadowIndexQuadrant = new(1024);
        private static List<int> sortedShadowIndices = new(1024);
        private static LinkedList<Segment> shadowClippingOccluders = new();
        private static List<MapEntity> entitiesForCulling = new(8192);
        private const int CONCURRENCY_LEVEL = 6;
        private static ConcurrentDictionary<MapEntity, bool> isEntityCulled = new(CONCURRENCY_LEVEL, 8192);
        private static Vector2 previousViewTargetPosition;
        private static ObjectPool<LinkedList<Segment>> poolLinkedListSegment = new(() => new());

        private static ParallelOptions cullingParallelOptions = new() { MaxDegreeOfParallelism = CONCURRENCY_LEVEL };
        private static Stopwatch cullingPerformanceTimer = new();

        public partial void InitializeProjSpecific()
        {
            quadrants.Add(Quadrant.RightTop, new(Vector2.Zero, Vector2.UnitX, Vector2.UnitY));
            quadrants.Add(Quadrant.LeftTop, new(Vector2.Zero, -Vector2.UnitX, Vector2.UnitY));
            quadrants.Add(Quadrant.LeftBottom, new(Vector2.Zero, -Vector2.UnitX, -Vector2.UnitY));
            quadrants.Add(Quadrant.RightBottom, new(Vector2.Zero, Vector2.UnitX, -Vector2.UnitY));
        }

        public static void PerformEntityCulling()
        {
            if (!GameMain.LightManager.LosEnabled
                || GameMain.LightManager.LosMode == LosMode.None
                || LightManager.ViewTarget is not Entity viewTarget
                || Screen.Selected?.Cam is not Camera camera)
            {
                if (cullingStateDirty)
                {
                    isEntityCulled.Clear();
                    cullingStateDirty = false;
                    previousViewTargetPosition = Vector2.Zero;
                }
                return;
            }

            if (previousViewTargetPosition == Vector2.Zero) { previousViewTargetPosition = viewTarget.Position; }
            Vector2 currentViewPosition = Timing.Interpolate(previousViewTargetPosition, viewTarget.Position);
            Vector2 viewDirection = currentViewPosition - previousViewTargetPosition;
            previousViewTargetPosition = currentViewPosition;

            if (lastCullingUpdateTime > Timing.TotalTime - CULLING_UPDATE_INTERVAL_SECONDS) { return; }

            cullingPerformanceTimer.Restart();

            int validShadowNumber = 0;
            Span<Segment> occluderClipBuffer = stackalloc Segment[3];

            shadowIndexLinkedList.Clear();
            shadowIndexQuadrant.Clear();
            isEntityCulled.Clear();

            Rectangle cameraViewBounds = camera.WorldView;

            Vector2 lightSourcePosition = Vector2.Zero;

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

                    if (convexHull.ParentEntity?.Submarine is Submarine parentSubmarine)
                    {
                        convexHullAABB.X += (int)parentSubmarine.DrawPosition.X;
                        convexHullAABB.Y += (int)parentSubmarine.DrawPosition.Y;
                    }

                    if (convexHullAABB.X > cameraViewBounds.X + cameraViewBounds.Width) { continue; }
                    if (convexHullAABB.X + convexHullAABB.Width < cameraViewBounds.X) { continue; }
                    if (convexHullAABB.Y < cameraViewBounds.Y - cameraViewBounds.Height) { continue; }
                    if (convexHullAABB.Y - convexHullAABB.Height > cameraViewBounds.Y) { continue; }

                    if (convexHull.ParentEntity is Item item
                        && item.GetComponent<Door>() is Door { OpenState: > 0.0f and < 1.0f })
                    {
                        continue;
                    }

                    VertexPositionColor[] shadowVertices = convexHull.ShadowVertices;

                    Vector2 occluderVertexStart = new(shadowVertices[0].Position.X, shadowVertices[0].Position.Y); // vertexPos1
                    Vector2 occluderVertexEnd = new(shadowVertices[1].Position.X, shadowVertices[1].Position.Y); // vertexPos0
                    Vector2 extrudedVertexEnd = new(shadowVertices[4].Position.X, shadowVertices[4].Position.Y); // extruded0
                    Vector2 extrudedVertexStart = new(shadowVertices[5].Position.X, shadowVertices[5].Position.Y); // extruded1

                    // true if ShadowVertices is reversed, we just reverse back.
                    if ((occluderVertexEnd - occluderVertexStart).LengthSquared() > (extrudedVertexEnd - extrudedVertexStart).LengthSquared())
                    {
                        (occluderVertexStart, extrudedVertexStart) = (extrudedVertexStart, occluderVertexStart);
                        (occluderVertexEnd, extrudedVertexEnd) = (extrudedVertexEnd, occluderVertexEnd);
                    }

                    if (lightSourcePosition == Vector2.Zero)
                    {
                        if (GetLineIntersection(
                            occluderVertexStart, extrudedVertexStart,
                            extrudedVertexEnd, occluderVertexEnd,
                            areLinesInfinite: true, out Vector2 intersection))
                        {
                            lightSourcePosition = intersection;
                        }
                        else
                        {
                            lightSourcePosition = viewTarget.DrawPosition;
                        }

                        foreach (RayRange quadrant in quadrants.Values)
                        {
                            quadrant.DoCaculate(lightSourcePosition);
                        }
                    }

                    if (validShadowNumber >= validShadowBuffer.Length)
                    {
                        Array.Resize(ref validShadowBuffer, validShadowBuffer.Length + 1024);
                    }

                    Vector2 occluderDirection = Vector2.Normalize(occluderVertexEnd - occluderVertexStart);

                    validShadowBuffer[validShadowNumber] = new(
                        lightSource: lightSourcePosition,
                        vertex1: occluderVertexEnd + occluderDirection * 1.0f,
                        vertex2: occluderVertexStart - occluderDirection * 1.0f
                    );

                    shadowIndexLinkedList.AddLast(validShadowNumber);

                    ref readonly Segment occluder = ref validShadowBuffer[validShadowNumber].Occluder;
                    Quadrant occluderQuadrant = Quadrant.None;
                    foreach (var (quadrant, rayRange) in quadrants)
                    {
                        if (occluder.IntersectWith(rayRange))
                        {
                            occluderQuadrant |= quadrant;
                        }
                    }

                    shadowIndexQuadrant.Add(validShadowNumber, occluderQuadrant);

                    validShadowNumber++;
                }
            }

            // Exclude the convex hulls whose sample points are all in shadow
            // When the number of convex hulls is sufficiently large,
            // sorting them based on their distance to the view target
            // and using the nearer hulls to prioritize determining whether farther hulls are in shadow
            // can significantly improve performance.
            shadowIndexLinkedList = new(shadowIndexLinkedList.OrderBy(
                shadowIndex => (lightSourcePosition - validShadowBuffer[shadowIndex].Occluder.Center).LengthSquared()
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
            if (viewDirection.Length() > 0.1f)
            {
                Vector2 predictedPosition = lightSourcePosition + viewDirection;
                foreach (int shadowIndex in sortedShadowIndices)
                {
                    ref Shadow shadow = ref validShadowBuffer[shadowIndex];
                    ref Segment occluder = ref shadow.Occluder;

                    float directionProjection = Vector2.Dot(occluder.StartToEnd, viewDirection);
                    if (Math.Abs(directionProjection) > 1e-2f)
                    {
                        float maximumTrimAmount = occluder.Length - 1.0f;
                        if (directionProjection < 0)
                        {
                            float predictionOffset = MathF.Min(MathF.Abs(PredictAngleChange(occluder.Start, lightSourcePosition, predictedPosition)) * SHADOW_PREDICTION_TOLERANCE_MULTIPLIER, maximumTrimAmount);
                            occluder.Start += Vector2.Normalize(occluder.StartToEnd) * predictionOffset;
                        }
                        else
                        {
                            float predictionOffset = MathF.Min(MathF.Abs(PredictAngleChange(occluder.End, lightSourcePosition, predictedPosition)) * SHADOW_PREDICTION_TOLERANCE_MULTIPLIER, maximumTrimAmount);
                            occluder.End += Vector2.Normalize(-occluder.StartToEnd) * predictionOffset;
                        }

                        shadow.DoCaculate(lightSourcePosition, occluder.Start, occluder.End);
                    }
                }
            }

            entitiesForCulling.Clear();
            entitiesForCulling.AddRange(Submarine.VisibleEntities);

            int totalCulled = 0;
            Parallel.For(
                fromInclusive: 0,
                toExclusive: (entitiesForCulling.Count + ENTITIES_PER_CULLING_BATCH - 1) / ENTITIES_PER_CULLING_BATCH,
                parallelOptions: cullingParallelOptions,
                body: ProcessCullingBatch
            );

            void ProcessCullingBatch(int batchIndex)
            {
                int batchStartIndex = batchIndex * ENTITIES_PER_CULLING_BATCH;
                int batchEndIndex = Math.Min(batchStartIndex + ENTITIES_PER_CULLING_BATCH, entitiesForCulling.Count);
                Span<Segment> entityEdges = stackalloc Segment[8];
                Span<Segment> edgeClipBuffer = stackalloc Segment[3];
                int entitiesCulled = 0;
                LinkedList<Segment> shadowClippingEdges = poolLinkedListSegment.Get();

                for (int entityIndex = batchStartIndex; entityIndex < batchEndIndex; entityIndex++)
                {
                    MapEntity entity = entitiesForCulling[entityIndex];
                    // The origin is at the top left
                    RectangleF entityAABB;

                    if (entity is Item item)
                    {
                        if (item.IsHidden
                            || !item.cachedVisibleExtents.HasValue
                            || item.IsLadder
                            || item.isWire)
                        {
                            continue;
                        }

                        entityAABB = item.cachedVisibleExtents.Value;
                        entityAABB.Offset(item.DrawPosition.X, item.DrawPosition.Y);

                        // In Vanilla, the AABB bounds calculation is messed up:
                        // ClientSource/Items/Item.cs | cachedVisibleExtents = extents = new Rectangle(min.ToPoint(), max.ToPoint());
                        // The correct one should be:
                        // cachedVisibleExtents = extents = new Rectangle(min.ToPoint(), (max - min).ToPoint());
                        // I have to calculate the sample points based on original cached extents in real-time here:
                        entityAABB.Width *= 2;
                        entityAABB.Height *= 2;
                        entityAABB.Y += entityAABB.Height;
                    }
                    else if (entity is Structure structure)
                    {
                        if (structure.IsHidden || structure.Prefab.DecorativeSprites.Length > 0)
                        {
                            continue;
                        }

                        entityAABB = Quad2D.FromSubmarineRectangle(structure.WorldRect).Rotated(
                            structure.FlippedX != structure.FlippedY
                                ? structure.RotationRad
                                : -structure.RotationRad).BoundingAxisAlignedRectangle;
                        entityAABB.Y += entityAABB.Height;
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
                                    goto ENTITY_CULLING_COMPLETE;
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
                            goto ENTITY_CULLING_COMPLETE;
                        }
                    }

                    isEntityCulled.TryAdd(entity, true);
                    entitiesCulled++;
                ENTITY_CULLING_COMPLETE:;
                }

                poolLinkedListSegment.Return(shadowClippingEdges);

                Interlocked.Add(ref totalCulled, entitiesCulled);
            }

            cullingPerformanceTimer.Stop();
            GameMain.PerformanceCounter.AddElapsedTicks("Mod:ShadowCulling", cullingPerformanceTimer.ElapsedTicks);

            if (DebugLog && Timing.TotalTime - lastPerformanceLogTime >= 2.0f)
            {
                float averageCullingTime = GameMain.PerformanceCounter.GetAverageElapsedMillisecs("Mod:ShadowCulling");
                LuaCsLogger.LogMessage($"Mod:ShadowCulling | Mean: {averageCullingTime} | Cull: {totalCulled}/{Submarine.VisibleEntities.Count()} | Shadows: {sortedShadowIndices.Count}/{validShadowNumber}");
                lastPerformanceLogTime = Timing.TotalTime;
            }

            lastCullingUpdateTime = Timing.TotalTime;

            cullingStateDirty = true;
        }

        public static float PredictAngleChange(
            in Vector2 referencePoint,
            in Vector2 currentPosition,
            in Vector2 predictedPosition)
        {
            Vector2 currentVector = currentPosition - referencePoint;
            Vector2 predictedVector = predictedPosition - referencePoint;

            return MathUtils.WrapAnglePi(MathF.Atan2(predictedVector.Y, predictedVector.X) - MathF.Atan2(currentVector.Y, currentVector.X));
        }

        public static bool GetLineIntersection(
            in Vector2 line1Point1, in Vector2 line1Point2,
            in Vector2 line2Point1, in Vector2 line2Point2,
            bool areLinesInfinite, out Vector2 intersectionPoint)
        {
            intersectionPoint = Vector2.Zero;
            Vector2 line1Direction = line1Point2 - line1Point1;
            Vector2 line2Direction = line2Point2 - line2Point1;
            float crossProduct = line1Direction.X * line2Direction.Y - line1Direction.Y * line2Direction.X;
            if (crossProduct == 0f)
            {
                return false;
            }

            Vector2 connectionVector = line2Point1 - line1Point1;
            float line1Parameter = (connectionVector.X * line2Direction.Y - connectionVector.Y * line2Direction.X) / crossProduct;
            if (!areLinesInfinite)
            {
                if (line1Parameter < 0f || line1Parameter > 1f)
                {
                    return false;
                }

                float line2Parameter = (connectionVector.X * line1Direction.Y - connectionVector.Y * line1Direction.X) / crossProduct;
                if (line2Parameter < 0f || line2Parameter > 1f)
                {
                    return false;
                }
            }

            intersectionPoint = line1Point1 + line1Parameter * line1Direction;
            return true;
        }

        // The drawing of light is managed by the LightManager,
        // and its rendering is independent of culling,
        // so we do not need to use its DrawSize to calculate the AABB.
        [HarmonyPatch(
             declaringType: typeof(LightComponent),
             methodName: nameof(LightComponent.DrawSize),
             methodType: MethodType.Getter
        )]
        class LightComponent_DrawSize
        {
            static bool Prefix(ref Vector2 __result)
            {
                __result.X = 0.0f;
                __result.Y = 0.0f;
                return false;
            }
        }
    }
}