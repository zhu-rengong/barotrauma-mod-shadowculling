using Barotrauma.Items.Components;
using ShadowCulling.Geometry;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ConvexHull = Barotrauma.Lights.ConvexHull;
using ConvexHullList = Barotrauma.Lights.ConvexHullList;
using LightManager = Barotrauma.Lights.LightManager;

namespace ShadowCulling;

public partial class Plugin
{
    private const int HullsPerBatch = 15;
    private const int EntitiesPerBatch = 65;
    private const float ShadowPredictionToleranceMultiplier = 1000.0f;
    public static int ParallelismLevel => Math.Min(Environment.ProcessorCount, 4);
    public static int ConcurrencyLevel => ParallelismLevel * 3;

    // Performance tracking
    private static Stopwatch cullingPerformanceTimer = new();
    private static double lastCullingUpdateTime;
    private static int ticksUntilNextCull;
    private static double lastPerformanceLogTime;

    // Shadow data buffers
    private static Shadow[] validShadowBuffer = new Shadow[512];
    private static int[] integerRangeBuffer = Enumerable.Range(0, validShadowBuffer.Length).ToArray();
    private static PooledLinkedList<int> shadowIndexLinkedList = new();
    private static Dictionary<Quadrant, RayRange> quadrants = new(4);
    private static List<int> sortedShadowIndices = new(1024);
    private static PooledLinkedList<Segment> shadowClippingOccluders = new();
    private static HashSet<int> predictableOccluderStart = new(1024);
    private static HashSet<int> predictableOccluderEnd = new(1024);

    // Entity lists for culling
    private static List<Hull> hullsForCulling = new(1024);
    private static List<MapEntity> entitiesForCulling = new(8192);
    private static List<Character> charactersForCulling = new(256);

    // Culling state
    private static bool isCullingStateDirty = false;

    // Entity culling state tracking
    private static ConcurrentDictionary<Entity, RectangleF> entityVisibleExtents = new(ConcurrencyLevel, 32768);
    private static ConcurrentDictionary<Entity, Hull?> entityHull = new(ConcurrencyLevel, 8192);
    private static ConcurrentDictionary<Entity, bool> isEntityCulled = new(ConcurrencyLevel, 8192);
    private static Vector2? previousViewInterpolatedPosition;

    // Object pooling for performance
    private static ObjectPool<PooledLinkedList<Segment>> segmentListPool = new(() => new());

    // Parallel processing configuration
    private static ParallelOptions cullingParallelOptions = new() { MaxDegreeOfParallelism = ParallelismLevel };

    // Public properties for external access
    public static Shadow[] ValidShadowBuffer => validShadowBuffer;
    public static List<int> SortedShadowIndices => sortedShadowIndices;
    public static List<Hull> HullsForCulling => hullsForCulling;
    public static ConcurrentDictionary<Entity, bool> IsEntityCulled => isEntityCulled;

    /// <summary>
    /// Determines whether culling should be disabled based on current game state.
    /// </summary>
    public static bool DisallowCulling =>
        Screen.Selected is { IsEditor: true }
        || !GameMain.LightManager.LosEnabled
        || GameMain.LightManager.LosMode == LosMode.None
        || (GameMain.IsSingleplayer
            ? GameMain.GameSession == null || !GameMain.GameSession.IsRunning
            : !GameMain.Client?.GameStarted ?? true);

    public partial void InitializeProjectSpecific()
    {
        quadrants.Add(Quadrant.RightTop, new RayRange(Vector2.Zero, Vector2.UnitX, Vector2.UnitY));
        quadrants.Add(Quadrant.LeftTop, new RayRange(Vector2.Zero, -Vector2.UnitX, Vector2.UnitY));
        quadrants.Add(Quadrant.LeftBottom, new RayRange(Vector2.Zero, -Vector2.UnitX, -Vector2.UnitY));
        quadrants.Add(Quadrant.RightBottom, new RayRange(Vector2.Zero, Vector2.UnitX, -Vector2.UnitY));
    }

    /// <summary>
    /// Clears all culling data if the state is dirty.
    /// </summary>
    public static void TryClearAll()
    {
        if (isCullingStateDirty)
        {
            Array.Clear(validShadowBuffer);
            hullsForCulling.Clear();
            entitiesForCulling.Clear();
            charactersForCulling.Clear();
            entityVisibleExtents.Clear();
            entityHull.Clear();
            isEntityCulled.Clear();
            previousViewInterpolatedPosition = null;
            DebugConsole.NewMessage("Culling data cleared!");
            isCullingStateDirty = false;
        }
    }

    public static void PerformEntityCulling(bool debug = false)
    {
        ticksUntilNextCull++;
        if (lastCullingUpdateTime <= Timing.TotalTime - CullingInterval)
        {
            cullingPerformanceTimer.Restart();
            bool success = DoCull(out int validShadowNumber, out int totalHullCulled, out int totalNonHullCulled);
            cullingPerformanceTimer.Stop();
            // Calculate as the average of ticks per frame
            GameMain.PerformanceCounter.AddElapsedTicks("Draw:ShadowCulling", cullingPerformanceTimer.ElapsedTicks / ticksUntilNextCull);

            if (success && DebugLoggingEnabled && Timing.TotalTime - lastPerformanceLogTime >= 2.0f)
            {
                float averageCullingTime = GameMain.PerformanceCounter.GetAverageElapsedMillisecs("Draw:ShadowCulling");
                DebugConsole.NewMessage(
                    $"Mean: {averageCullingTime:F2}ms | " +
                    $"Cull(Hull): {totalHullCulled}/{hullsForCulling.Count} | " +
                    $"Cull(NonHull): {totalNonHullCulled}/{entitiesForCulling.Count + charactersForCulling.Count} | " +
                    $"Shadows: {sortedShadowIndices.Count}/{validShadowNumber}");
                lastPerformanceLogTime = Timing.TotalTime;
            }

            ticksUntilNextCull = 0;
            lastCullingUpdateTime = Timing.TotalTime;
        }

        bool DoCull(out int validShadowNumber, out int totalHullCulled, out int totalNonHullCulled)
        {
            validShadowNumber = 0;
            totalHullCulled = 0;
            totalNonHullCulled = 0;

            if (!debug && !CullingEnabled) { return false; }

            if (DisallowCulling
                || LightManager.ViewTarget is not Entity viewTarget
                || Screen.Selected?.Cam is not Camera camera)
            {
                TryClearAll();
                return false;
            }

            Vector2 viewTargetPosition = GetViewTargetPosition(viewTarget);
            Vector2 viewInterpolatedPosition = GetViewInterpolatedPosition(viewTarget, viewTargetPosition, out Vector2 viewDirection);

            UpdateQuadrantOrigins(viewTargetPosition);

            CollectVisibleShadows(viewTargetPosition, camera, out validShadowNumber);
            FilterOutOccludedShadows(viewTargetPosition);
            ApplyShadowPrediction(viewTargetPosition, viewDirection);

            CullEntities(viewTarget, camera, out totalHullCulled, out totalNonHullCulled);

            isCullingStateDirty = true;

            return true;
        }
    }

    /// <summary>
    /// Gets the actual position to use as the view target.
    /// </summary>
    private static Vector2 GetViewTargetPosition(Entity viewTarget)
    {
        if (viewTarget is Character viewTargetCharacter
            && viewTargetCharacter.AnimController?.GetLimb(LimbType.Head) is Limb head
            && !head.IsSevered && !head.Removed)
        {
            return head.body.DrawPosition;
        }
        return viewTarget.DrawPosition;
    }

    /// <summary>
    /// Gets the interpolated position of the view relative to the submarine if applicable.
    /// </summary>
    private static Vector2 GetViewInterpolatedPosition(Entity viewTarget, in Vector2 viewTargetCorrectedPosition, out Vector2 viewDirection)
    {
        Vector2 targetPosition = viewTargetCorrectedPosition;
        if (viewTarget.Submarine != null)
        {
            targetPosition -= viewTarget.Submarine.DrawPosition;
        }

        if (!previousViewInterpolatedPosition.HasValue || (targetPosition - previousViewInterpolatedPosition.Value).LengthSquared() > 1e6f)
        {
            previousViewInterpolatedPosition = targetPosition;
        }

        // Apply interpolation for smooth movement
        Vector2 viewInterpolatedPosition = previousViewInterpolatedPosition.Value * 0.9f + targetPosition * 0.1f;

        viewDirection = viewInterpolatedPosition - previousViewInterpolatedPosition.Value;
        previousViewInterpolatedPosition = viewInterpolatedPosition;

        return viewInterpolatedPosition;
    }

    private static void UpdateQuadrantOrigins(Vector2 origin)
    {
        foreach (RayRange quadrant in quadrants.Values)
        {
            quadrant.UpdateOrigin(origin);
        }
    }

    /// <summary>
    /// Collects all visible shadows from convex hulls within the camera view.
    /// </summary>
    private static void CollectVisibleShadows(in Vector2 viewTargetPosition, Camera camera, out int validShadowNumber)
    {
        validShadowNumber = 0;
        Rectangle cameraViewBounds = camera.WorldView;

        foreach (ConvexHullList hullList in ConvexHull.HullLists)
        {
            foreach (ConvexHull convexHull in hullList.List)
            {
                // Checks if a convex hull is valid for shadow casting.
                if (convexHull.IsInvalid || !convexHull.Enabled || convexHull.ShadowVertexCount < 6) { continue; }

                Rectangle convexHullAABB = convexHull.BoundingBox;
                // In world coordinates, the origin of ConvexHull.BoundingBox is assumed to be left-bottom corner
                // (perhaps due to historical reasons). We need to convert its origin to the top-left corner
                // to maintain consistency with other world rectangles.
                convexHullAABB.Y += convexHullAABB.Height;

                // Gets the offset to convert from local to world coordinates.
                Vector2 offsetToWorld = Vector2.Zero;
                if (convexHull.ParentEntity?.Submarine is Submarine parentSubmarine)
                {
                    offsetToWorld.X = parentSubmarine.DrawPosition.X;
                    offsetToWorld.Y = parentSubmarine.DrawPosition.Y;
                }

                // Checks if the convex hull overlaps with the camera view.
                convexHullAABB.X += (int)offsetToWorld.X;
                convexHullAABB.Y += (int)offsetToWorld.Y;

                if (convexHullAABB.X > cameraViewBounds.X + cameraViewBounds.Width
                    || convexHullAABB.X + convexHullAABB.Width < cameraViewBounds.X
                    || convexHullAABB.Y < cameraViewBounds.Y - cameraViewBounds.Height
                    || convexHullAABB.Y - convexHullAABB.Height > cameraViewBounds.Y)
                {
                    continue;
                }


                Vector2 vertex0Position = convexHull.losVertices[0].Pos + convexHull.losOffsets[0] + offsetToWorld;
                Vector2 vertex1Position = convexHull.losVertices[1].Pos + convexHull.losOffsets[1] + offsetToWorld;

                if (Vector2.DistanceSquared(vertex0Position, vertex1Position) < 1.0f) { continue; }

                Vector2 occluderVertexUnitOffset = Vector2.Normalize(vertex1Position - vertex0Position);

                // Ensures the shadow buffer has enough capacity.
                if (validShadowNumber >= validShadowBuffer.Length)
                {
                    Array.Resize(ref validShadowBuffer, validShadowBuffer.Length + 128);
                    EnsureIntRangeCapacity(validShadowBuffer.Length);
                }

                validShadowBuffer[validShadowNumber] = new(
                    convexHull,
                    lightSource: viewTargetPosition,
                    vertex1: vertex0Position - occluderVertexUnitOffset,
                    vertex2: vertex1Position + occluderVertexUnitOffset
                );

                ref Shadow shadow = ref validShadowBuffer[validShadowNumber];
                ref Segment occluder = ref shadow.Occluder;

                // Transforms shadows for doors, adjusting for open/close state.
                if (convexHull.ParentEntity is Item item
                    && item.GetComponent<Door>() is Door { OpenState: > 0.0f and < 1.0f } door)
                {
                    float doorStateDelta = (door.IsOpen ? door.OpeningSpeed : door.ClosingSpeed) * (float)Timing.Step;

                    Vector2 doorLosVertexOffset = 0.5f
                        * occluderVertexUnitOffset
                        * MathF.Min(
                            MathF.Max(0.0f, occluder.Length - doorStateDelta),
                            800.0f * doorStateDelta);

                    occluder.End -= doorLosVertexOffset;
                    occluder.Start += doorLosVertexOffset;
                    shadow.Recalculate(viewTargetPosition, occluder.Start, occluder.End);
                }

                shadow.DistanceToView = (viewTargetPosition - occluder.Center).LengthSquared();

                // Which quadrants does the shadow occluder cover.
                Quadrant occluderQuadrant = Quadrant.None;
                foreach (var (quadrant, rayRange) in quadrants)
                {
                    if (occluder.IntersectWith(rayRange))
                    {
                        occluderQuadrant |= quadrant;
                    }
                }
                shadow.OccluderQuadrants = occluderQuadrant;

                validShadowNumber++;
            }
        }

        CollectionsMarshal.SetCount(sortedShadowIndices, validShadowNumber);
        integerRangeBuffer.AsSpan()
            .Slice(0, validShadowNumber)
            .CopyTo(CollectionsMarshal.AsSpan(sortedShadowIndices));
    }

    private static void EnsureIntRangeCapacity(int capacity)
    {
        int sizeBeforeResize = integerRangeBuffer.Length;
        if (sizeBeforeResize < capacity)
        {
            Array.Resize(ref integerRangeBuffer, capacity);
            for (int i = sizeBeforeResize; i < capacity; i++)
            {
                integerRangeBuffer[i] = i;
            }
        }
    }

    /// <summary>
    /// Filters out shadows that are occluded by other shadows.
    /// </summary>
    private static void FilterOutOccludedShadows(in Vector2 viewTargetPosition)
    {
        // Use nearer convex hulls to prioritize determining whether farther ones are in shadow,
        // this can significantly improve the hit rate of prediction.
        sortedShadowIndices.Sort((s1, s2) => validShadowBuffer[s1].DistanceToView.CompareTo(validShadowBuffer[s2].DistanceToView));

        shadowIndexLinkedList.Clear(returnNode: true);
        foreach (int index in sortedShadowIndices)
        {
            shadowIndexLinkedList.AddLast(index);
        }

        Span<Segment> clipBuffer = stackalloc Segment[3];
        PooledLinkedListNode<int>? currentShadowNode = shadowIndexLinkedList.Last;

        while (currentShadowNode != null)
        {
            var previousShadowNode = currentShadowNode.Previous;
            int currentShadowIndex = currentShadowNode.Value;
            ref readonly Shadow currentShadow = ref validShadowBuffer[currentShadowIndex];
            ref readonly Segment entireOccluder = ref currentShadow.Occluder;
            Quadrant quadrants = currentShadow.OccluderQuadrants;

            shadowClippingOccluders.AddLast(entireOccluder);
            shadowIndexLinkedList.Remove(currentShadowNode);

            // Check if this shadow is occluded by remaining shadows
            foreach (int otherShadowIndex in shadowIndexLinkedList)
            {
                ref readonly Shadow otherShadow = ref validShadowBuffer[otherShadowIndex];

                if (!quadrants.HasAnyFlag(otherShadow.OccluderQuadrants)) { continue; }

                PooledLinkedListNode<Segment>? clipNode = shadowClippingOccluders.First;
                if (clipNode == null) { break; }

                do
                {
                    var nextClipNode = clipNode.Next;
                    ref readonly Segment occluder = ref clipNode.ValueRef;
                    // Clips the occluder against every shadows, replacing it with the resulting clipped segments.
                    int clipCount = occluder.ClipFrom(otherShadow, clipBuffer);
                    if (clipCount != 1 || occluder != clipBuffer[0])
                    {
                        for (int clipIndex = 0; clipIndex < clipCount; clipIndex++)
                        {
                            shadowClippingOccluders.AddBefore(clipNode, clipBuffer[clipIndex]);
                        }
                        shadowClippingOccluders.Remove(clipNode, returnNode: true);
                    }
                    clipNode = nextClipNode;
                } while (clipNode != null);
            }

            // Re-add if not fully occluded
            if (shadowClippingOccluders.Count > 0)
            {
                // Reinserts the shadow node back into the linked list at the appropriate position.
                if (previousShadowNode != null)
                {
                    shadowIndexLinkedList.AddAfter(previousShadowNode, currentShadowNode);
                }
                else
                {
                    shadowIndexLinkedList.AddFirst(currentShadowNode);
                }
            }
            else
            {
                shadowIndexLinkedList.ReturnNode(currentShadowNode);
            }

            shadowClippingOccluders.Clear(returnNode: true);
            currentShadowNode = previousShadowNode;
        }

        sortedShadowIndices.Clear();
        sortedShadowIndices.AddRange(shadowIndexLinkedList);
    }

    /// <summary>
    /// Applies shadow prediction based on view movement to avoid pop-in effects.
    /// </summary>
    private static void ApplyShadowPrediction(in Vector2 viewTargetPosition, in Vector2 viewDirection)
    {
        if (viewDirection.LengthSquared() <= 0.01f) { return; }

        Vector2 predictedPosition = viewTargetPosition + viewDirection;

        // Identifies which occluders are likely to move based on view direction.
        predictableOccluderStart.Clear();
        predictableOccluderEnd.Clear();
        foreach (int currentShadowIndex in sortedShadowIndices)
        {
            ref Segment currentOccluder = ref validShadowBuffer[currentShadowIndex].Occluder;

            Vector2 startToView = viewTargetPosition - currentOccluder.Start;
            if (startToView.CrossProduct(currentOccluder.StartToEnd) * startToView.CrossProduct(viewDirection) < 0.0f)
            {
                predictableOccluderStart.Add(currentShadowIndex);
            }

            Vector2 endToView = viewTargetPosition - currentOccluder.End;
            if (endToView.CrossProduct(currentOccluder.StartToEnd) * endToView.CrossProduct(viewDirection) > 0.0f)
            {
                predictableOccluderEnd.Add(currentShadowIndex);
            }
        }

        foreach (int currentShadowIndex in sortedShadowIndices)
        {
            ref Shadow currentShadow = ref validShadowBuffer[currentShadowIndex];
            ref Segment currentOccluder = ref currentShadow.Occluder;

            // Applies prediction to the start point of an occluder.
            if (predictableOccluderStart.Contains(currentShadowIndex))
            {
                foreach (int otherShadowIndex in sortedShadowIndices)
                {
                    if (currentShadowIndex == otherShadowIndex) { continue; }

                    ref readonly Shadow otherShadow = ref validShadowBuffer[otherShadowIndex];
                    ref readonly Segment otherOccluder = ref otherShadow.Occluder;

                    // Checks if start point prediction is valid for this occluder.
                    if (otherOccluder.ToPointDistanceSquared(currentOccluder.Start) >= 100.0f) { continue; }

                    bool isOtherStartCloseEnough = (otherOccluder.Start - currentOccluder.Start).LengthSquared() < 100.0f;
                    bool isOtherEndCloseEnough = (otherOccluder.End - currentOccluder.Start).LengthSquared() < 100.0f;

                    if ((!isOtherStartCloseEnough && !isOtherEndCloseEnough)
                        || (isOtherStartCloseEnough && !predictableOccluderStart.Contains(otherShadowIndex))
                        || (isOtherEndCloseEnough && !predictableOccluderEnd.Contains(otherShadowIndex)))
                    {
                        goto SKIP_PREDICATION;
                    }
                }

                float predictionOffset = MathF.Min(
                    MathF.Abs((viewTargetPosition - currentOccluder.Start).VectorAngle(predictedPosition - currentOccluder.Start)) * ShadowPredictionToleranceMultiplier,
                    currentOccluder.Length - 1.0f);

                currentOccluder.Start += Vector2.Normalize(currentOccluder.StartToEnd) * predictionOffset;
                currentShadow.Recalculate(viewTargetPosition, currentOccluder.Start, currentOccluder.End);
            SKIP_PREDICATION:;
            }

            // Applies prediction to the end point of an occluder.
            if (predictableOccluderEnd.Contains(currentShadowIndex))
            {
                foreach (int otherShadowIndex in sortedShadowIndices)
                {
                    if (currentShadowIndex == otherShadowIndex) { continue; }

                    ref readonly Shadow otherShadow = ref validShadowBuffer[otherShadowIndex];
                    ref readonly Segment otherOccluder = ref otherShadow.Occluder;

                    // Checks if end point prediction is valid for this occluder.
                    if (otherOccluder.ToPointDistanceSquared(currentOccluder.End) >= 100.0f) { continue; }

                    bool isOtherStartCloseEnough = (otherOccluder.Start - currentOccluder.End).LengthSquared() < 100.0f;
                    bool isOtherEndCloseEnough = (otherOccluder.End - currentOccluder.End).LengthSquared() < 100.0f;

                    if ((!isOtherStartCloseEnough && !isOtherEndCloseEnough)
                        || (isOtherStartCloseEnough && !predictableOccluderStart.Contains(otherShadowIndex))
                        || (isOtherEndCloseEnough && !predictableOccluderEnd.Contains(otherShadowIndex)))
                    {
                        goto SKIP_PREDICATION;
                    }
                }

                float predictionOffset = MathF.Min(
                    MathF.Abs((viewTargetPosition - currentOccluder.End).VectorAngle(predictedPosition - currentOccluder.End)) * ShadowPredictionToleranceMultiplier,
                    currentOccluder.Length - 1.0f);

                currentOccluder.End += Vector2.Normalize(-currentOccluder.StartToEnd) * predictionOffset;
                currentShadow.Recalculate(viewTargetPosition, currentOccluder.Start, currentOccluder.End);
            SKIP_PREDICATION:;
            }
        }
    }

    /// <summary>
    /// Performs culling on all entities and returns the count of culled entities.
    /// </summary>
    private static void CullEntities(Entity viewTarget, Camera camera, out int totalHullCulled, out int totalNonHullCulled)
    {
        int _totalHullCulled = 0;
        int _totalNonHullCulled = 0;
        isEntityCulled.Clear();

        hullsForCulling.Clear();
        foreach (Hull hull in Hull.HullList)
        {
            if (hull.Submarine is Submarine sub && Submarine.visibleSubs.Contains(sub)
                && hull.Volume > 40000.0f && hull.RectWidth > 200.0f && hull.RectHeight > 200.0f
                && Submarine.RectsOverlap(hull.WorldRect, camera.WorldView))
            {
                hullsForCulling.Add(hull);
            }
        }

        if (hullsForCulling.Count > HullsPerBatch)
        {
            Parallel.For(
                fromInclusive: 0,
                toExclusive: (hullsForCulling.Count + HullsPerBatch - 1) / HullsPerBatch,
                parallelOptions: cullingParallelOptions,
                body: index =>
                {
                    int startIndex = index * HullsPerBatch;
                    Cull(viewTarget, hullsForCulling, startIndex, Math.Min(startIndex + HullsPerBatch, hullsForCulling.Count), ref _totalHullCulled);
                }
            );
        }
        else
        {
            Cull(viewTarget, hullsForCulling, 0, hullsForCulling.Count, ref _totalHullCulled);
        }

        entitiesForCulling.Clear();
        entitiesForCulling.AddRange(Submarine.visibleEntities);

        if (entitiesForCulling.Count > EntitiesPerBatch)
        {
            Parallel.For(
                fromInclusive: 0,
                toExclusive: (entitiesForCulling.Count + EntitiesPerBatch - 1) / EntitiesPerBatch,
                parallelOptions: cullingParallelOptions,
                body: index =>
                {
                    int startIndex = index * EntitiesPerBatch;
                    Cull(viewTarget, entitiesForCulling, startIndex, Math.Min(startIndex + EntitiesPerBatch, entitiesForCulling.Count), ref _totalNonHullCulled);
                }
            );
        }
        else
        {
            Cull(viewTarget, entitiesForCulling, 0, entitiesForCulling.Count, ref _totalNonHullCulled);
        }

        charactersForCulling.Clear();
        charactersForCulling.AddRange(Character.CharacterList.Where(c => c.IsVisible));

        Cull(viewTarget, charactersForCulling, 0, charactersForCulling.Count, ref _totalNonHullCulled);

        totalNonHullCulled = _totalNonHullCulled;
        totalHullCulled = _totalHullCulled;
    }

    /// <summary>
    /// Culls a batch of entities.
    /// </summary>
    private static void Cull<T>(Entity viewTarget, List<T> entities, int fromInclusive, int toExclusive, ref int totalCulled) where T : Entity
    {
        Span<Segment> entityEdges = stackalloc Segment[8];
        Span<Segment> edgeClipBuffer = stackalloc Segment[3];
        PooledLinkedList<Segment> clippingEdges = segmentListPool.Get();
        int entitiesCulled = 0;

        for (int index = fromInclusive; index < toExclusive; index++)
        {
            T entity = entities[index];

            RectangleF entityAABB;

            // Gets the AABB for the entity based on its type.
            if (typeof(T) == typeof(Hull) && entity is Hull hull)
            {
                if (hull.BallastFlora is not null) { continue; }
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
                    entityAABB.Offset(item.DrawPosition);

                    // Check if item is inside a culled hull
                    if (item.CurrentHull is Hull itemHull && isEntityCulled.TryGetValue(itemHull, out bool _))
                    {
                        RectangleF hullAABB = itemHull.WorldRect;
                        if (entityAABB.X > hullAABB.X
                            && entityAABB.Y < hullAABB.Y
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

                    if (!entityVisibleExtents.TryGetValue(structure, out RectangleF extents))
                    {
                        entityVisibleExtents.TryAdd(structure, extents = AABB.CalculateFixed(structure));
                    }

                    entityAABB = extents;
                    entityAABB.Offset(structure.DrawPosition);

                    if (!entityHull.TryGetValue(structure, out Hull? structureHull))
                    {
                        entityHull.TryAdd(structure, structureHull = Hull.FindHull(structure.WorldPosition));
                    }

                    if (structureHull != null && isEntityCulled.TryGetValue(structureHull, out bool _))
                    {
                        RectangleF hullAABB = structureHull.WorldRect;
                        if (entityAABB.X > hullAABB.X
                          && entityAABB.Y < hullAABB.Y
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

            // Calculates the edge segments for the entity's AABB.
            Vector2 leftTop = new(entityAABB.X, entityAABB.Y);
            Vector2 rightTop = new(entityAABB.X + entityAABB.Width, entityAABB.Y);
            Vector2 leftBottom = new(entityAABB.X, entityAABB.Y - entityAABB.Height);
            Vector2 rightBottom = new(rightTop.X, leftBottom.Y);

            entityEdges[4] = new Segment(leftTop, rightTop);
            entityEdges[5] = new Segment(rightTop, rightBottom);
            entityEdges[6] = new Segment(rightBottom, leftBottom);
            entityEdges[7] = new Segment(leftBottom, leftTop);

            // Which quadrants does the entity covers.
            Quadrant entityQuadrant = Quadrant.None;
            int numCoveredQuadrants = 0;
            foreach (var (quadrant, rayRange) in quadrants)
            {
                for (int edgeIndex = 4; edgeIndex < 8; edgeIndex++)
                {
                    ref readonly Segment edge = ref entityEdges[edgeIndex];
                    if (edge.IntersectWith(rayRange))
                    {
                        if (++numCoveredQuadrants > 2) { goto SKIP; }
                        entityQuadrant |= quadrant;
                        break;
                    }
                }
            }

            // Selects the relevant edges based on the quadrant.
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

            // Checks if an entity is visible by testing its edges against shadows.
            for (int edgeIndex = 0; edgeIndex < edgeCount; edgeIndex++)
            {
                clippingEdges.AddLast(entityEdges[edgeIndex]);

                foreach (int shadowIndex in sortedShadowIndices)
                {
                    ref readonly Shadow shadow = ref validShadowBuffer[shadowIndex];
                    if (!entityQuadrant.HasAnyFlag(shadow.OccluderQuadrants)) { continue; }

                    PooledLinkedListNode<Segment>? clipNode = clippingEdges.First;
                    if (clipNode == null) { break; }

                    do
                    {
                        var nextClipNode = clipNode.Next;
                        ref readonly Segment edge = ref clipNode.ValueRef;
                        int clipCount = edge.ClipFrom(shadow, edgeClipBuffer);
                        if (clipCount != 1 || edge != edgeClipBuffer[0])
                        {
                            for (int clipIndex = 0; clipIndex < clipCount; clipIndex++)
                            {
                                clippingEdges.AddBefore(clipNode, edgeClipBuffer[clipIndex]);
                            }
                            clippingEdges.Remove(clipNode, returnNode: true);
                        }
                        clipNode = nextClipNode;
                    } while (clipNode != null);
                }

                bool refuseCulling = clippingEdges.Count > 0;

                clippingEdges.Clear(returnNode: true);

                if (refuseCulling)
                {
                    goto SKIP;
                }
            }

        CULL:;
            isEntityCulled.TryAdd(entity, true);
            entitiesCulled++;

        SKIP:;
        }

        segmentListPool.Return(clippingEdges);
        Interlocked.Add(ref totalCulled, entitiesCulled);
    }
}
