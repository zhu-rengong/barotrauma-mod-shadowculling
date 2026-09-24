using Barotrauma.Items.Components;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ConvexHull = Barotrauma.Lights.ConvexHull;
using ConvexHullList = Barotrauma.Lights.ConvexHullList;
using LightManager = Barotrauma.Lights.LightManager;

namespace ShadowCulling;

public partial class Plugin
{
    private const float ShadowPredictionToleranceMultiplier = 1000.0f;

    private const float PredictionNeighborDistance = 10.0f;
    private const float PredictionNeighborDistanceSquared = PredictionNeighborDistance * PredictionNeighborDistance;

    // Widens the circle prefilter below so it can never skip a pair the exact test would accept.
    private const float PredictionNeighborSlack = 1.0f;

    private static int PartitionRangeSize => 50;
    private static int ParallelTolerance => (int)(PartitionRangeSize * 1.5);
    public static int ParallelismLevel => Math.Min(Environment.ProcessorCount, 4);

    // Performance tracking
    private static Stopwatch cullingPerformanceTimer = new();
    public static bool IsCullPerformable;
    public static double LastCullingUpdateTime;
    public static int TicksUntilNextCull;
    public static long CullTickAccumulator;
    private static double lastPerformanceLogTime;

    // Shadow data buffers
    private static Shadow[] validShadowBuffer = new Shadow[512];
    private static int[] integerRangeBuffer = Enumerable.Range(0, validShadowBuffer.Length).ToArray();
    // Per shadow: the squared distance from its occluder's center beyond which a point cannot be near it, filled
    // by ApplyShadowPrediction alongside the buffer above.
    private static float[] occluderNeighborReachSquared = new float[validShadowBuffer.Length];
    // Per shadow: its quadrant coverage, in its own array so the filtering loops never touch the ~100-byte Shadow.
    private static Quadrant[] shadowQuadrants = new Quadrant[validShadowBuffer.Length];
    private static PooledLinkedList<int> shadowIndexLinkedList = new();
    // The four 90° sectors of the view, in probe order. An array: both hot loops walk all four.
    private static QuadrantRayRange[] quadrants = [];
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
    private static AttachedProperty<RectangleF> entityVisibleExtents = AttachedProperty<RectangleF>.Create();
    private static AttachedProperty<Hull?> entityHull = AttachedProperty<Hull?>.Create();
    private static AttachedProperty<bool> isEntityCulled = AttachedProperty<bool>.Create(false);
    private static Vector2? previousViewInterpolatedPosition;

    public static Vector2 ViewPosHijacked;

    // Each worker thread keeps a small cache of clipping lists, and every list is emptied before it is stored again,
    // so its nodes stay pooled.
    private static ObjectPool<PooledLinkedList<Segment>> segmentListPool = new(
        static () => new PooledLinkedList<Segment>(),
        onReturn: static list => list.Clear());

    // Public properties for external access
    public static Shadow[] ValidShadowBuffer => validShadowBuffer;
    public static List<int> SortedShadowIndices => sortedShadowIndices;
    public static List<Hull> HullsForCulling => hullsForCulling;
    public static AttachedProperty<RectangleF> EntityVisibleExtents => entityVisibleExtents;
    public static AttachedProperty<bool> IsEntityCulled => isEntityCulled;

    public static bool IsDrawingInMainViewport { get; set; } = false;

    /// <summary>Determines whether culling should be allowed based on current game state.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool IsCullingAllowed()
    {
        bool result = CullingEnabled
            && IsDrawingInMainViewport
            && GameMain.LightManager.LosEnabled
            && GameMain.LightManager.LosMode != LosMode.None
            && (GameMain.IsSingleplayer
                ? GameMain.GameSession?.IsRunning ?? false
                : GameMain.Client?.GameStarted ?? false);
        return result;
    }

    /// <summary>One of the four sectors of the view, together with the ray range that covers it.</summary>
    private struct QuadrantRayRange
    {
        /// <summary>The quadrant flag that stands for this sector.</summary>
        public Quadrant Quadrant;

        /// <summary>The rays bounding the sector, re-anchored at the view position on every cull.</summary>
        public RayRange Range;

        public QuadrantRayRange(Quadrant quadrant, in RayRange range)
        {
            Quadrant = quadrant;
            Range = range;
        }
    }

    public partial void InitializeProjectSpecific()
    {
        quadrants =
        [
            new(Quadrant.RightTop, new RayRange(Vector2.Zero, Vector2.UnitX, Vector2.UnitY)),
            new(Quadrant.LeftTop, new RayRange(Vector2.Zero, -Vector2.UnitX, Vector2.UnitY)),
            new(Quadrant.LeftBottom, new RayRange(Vector2.Zero, -Vector2.UnitX, -Vector2.UnitY)),
            new(Quadrant.RightBottom, new RayRange(Vector2.Zero, Vector2.UnitX, -Vector2.UnitY)),
        ];
    }

    /// <summary>Clears all culling data if the state is dirty.</summary>
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

    public static void CacheStructureVisibleExtents(Structure structure, Vector2 max, Vector2 min)
    {
        RectangleF extents = new RectangleF(MathF.Min(max.X, min.X), MathF.Max(max.Y, min.Y), MathF.Abs(max.X - min.X), MathF.Abs(max.Y - min.Y));
        extents.Offset(-structure.WorldPosition);
        entityVisibleExtents.SetValue(structure, extents);
    }

    public static void PerformEntityCulling()
    {
        cullingPerformanceTimer.Restart();
        bool success = DoCull(out int validShadowNumber);
        cullingPerformanceTimer.Stop();
        CullTickAccumulator += cullingPerformanceTimer.ElapsedTicks / TicksUntilNextCull;

        if (success && DebugLoggingEnabled && Timing.TotalTime - lastPerformanceLogTime >= 2.0f)
        {
            float averageCullingTime = GameMain.PerformanceCounter.GetAverageElapsedMillisecs("Draw:ShadowCulling");
            DebugConsole.NewMessage(
                $"Mean: {averageCullingTime:F2}ms | " +
                $"Cull(Hull): {totalHullCulled}/{hullsForCulling.Count} | " +
                $"Cull(NonHull): {totalNonHullCulled}/{entitiesForCulling.Count + charactersForCulling.Count} | " +
                $"Shadows: {sortedShadowIndices.Count}/{validShadowNumber} | " +
                $"ClipPool: {segmentListPool.Statistics}");
            lastPerformanceLogTime = Timing.TotalTime;
        }

        bool DoCull(out int validShadowNumber)
        {
            validShadowNumber = 0;

            if (LightManager.ViewTarget is not Entity viewTarget || Screen.Selected?.Cam is not Camera camera)
            {
                TryClearAll();
                return false;
            }

            Vector2 viewTargetPosition = ViewPosHijacked;
            _ = GetViewInterpolatedPosition(viewTarget, viewTargetPosition, out Vector2 viewDirection);

            UpdateQuadrantOrigins(viewTargetPosition);

            CollectVisibleShadows(viewTargetPosition, camera, out validShadowNumber);
            FilterOutOccludedShadows();
            ApplyShadowPrediction(viewTargetPosition, viewDirection);

            CullEntities(camera);

            isCullingStateDirty = true;

            return true;
        }
    }

    /// <summary>Gets the interpolated position of the view relative to the submarine if applicable.</summary>
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
        for (int i = 0; i < quadrants.Length; i++)
        {
            ref RayRange range = ref quadrants[i].Range;
            range.UpdateOrigin(origin);
        }
    }

    /// <summary>Collects all visible shadows from convex hulls within the camera view.</summary>
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

                // Skip doors whose open state has changed.
                if (convexHull.ParentEntity is Item item
                    && item.GetComponent<Door>() is Door door
                    && (door.OpenState - door.lastOpenState) != 0.0f)
                {
                    continue;
                }

                Vector2 vertex0Position = convexHull.losVertices[0].Pos + convexHull.losOffsets[0] + offsetToWorld;
                Vector2 vertex1Position = convexHull.losVertices[1].Pos + convexHull.losOffsets[1] + offsetToWorld;

                if (Vector2.DistanceSquared(vertex0Position, vertex1Position) < 1.0f) { continue; }

                Vector2 occluderVertexUnitOffset = Vector2.Normalize(vertex1Position - vertex0Position);

                if (validShadowNumber >= validShadowBuffer.Length)
                {
                    int capacity = validShadowBuffer.Length + 128;
                    Array.Resize(ref validShadowBuffer, capacity);
                    Array.Resize(ref occluderNeighborReachSquared, capacity);
                    Array.Resize(ref shadowQuadrants, capacity);
                    EnsureIntRangeCapacity(capacity);
                }

                validShadowBuffer[validShadowNumber] = new(
                    convexHull,
                    lightSource: viewTargetPosition,
                    vertex1: vertex0Position - occluderVertexUnitOffset,
                    vertex2: vertex1Position + occluderVertexUnitOffset
                );

                ref Shadow shadow = ref validShadowBuffer[validShadowNumber];
                ref Segment occluder = ref shadow.Occluder;

                shadow.DistanceToView = (viewTargetPosition - occluder.Center).LengthSquared();

                // Which quadrants does the shadow occluder cover.
                Quadrant occluderQuadrant = Quadrant.None;
                for (int quadrantIndex = 0; quadrantIndex < quadrants.Length; quadrantIndex++)
                {
                    ref readonly QuadrantRayRange quadrant = ref quadrants[quadrantIndex];
                    if (occluder.IntersectWith(quadrant.Range))
                    {
                        occluderQuadrant |= quadrant.Quadrant;
                    }
                }
                shadow.OccluderQuadrants = occluderQuadrant;
                shadowQuadrants[validShadowNumber] = occluderQuadrant;

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

    /// <summary>Filters out shadows that are occluded by other shadows.</summary>
    private static void FilterOutOccludedShadows()
    {
        // Use nearer convex hulls to prioritize determining whether farther ones are in shadow,
        // this can significantly improve the hit rate of prediction.
        sortedShadowIndices.Sort((s1, s2) => validShadowBuffer[s1].DistanceToView.CompareTo(validShadowBuffer[s2].DistanceToView));

        shadowIndexLinkedList.Clear();
        foreach (int index in CollectionsMarshal.AsSpan(sortedShadowIndices))
        {
            shadowIndexLinkedList.AddLast(index);
        }

        Span<Segment> clipBuffer = stackalloc Segment[3];
        PooledLinkedListNode currentShadowNode = shadowIndexLinkedList.Last;

        while (currentShadowNode.IsValid)
        {
            PooledLinkedListNode previousShadowNode = shadowIndexLinkedList.Previous(currentShadowNode);
            int currentShadowIndex = shadowIndexLinkedList[currentShadowNode];
            ref readonly Shadow currentShadow = ref validShadowBuffer[currentShadowIndex];
            ref readonly Segment entireOccluder = ref currentShadow.Occluder;
            Quadrant quadrants = currentShadow.OccluderQuadrants;

            shadowClippingOccluders.AddLast(entireOccluder);
            // Takes the node out of the ring for the duration of the test below, but keeps its slot alive so that
            // it can be put back right where it was when the shadow turns out not to be fully occluded.
            shadowIndexLinkedList.Detach(currentShadowNode);

            // Check if this shadow is occluded by remaining shadows
            foreach (int otherShadowIndex in shadowIndexLinkedList)
            {
                if (!quadrants.HasAnyFlag(shadowQuadrants[otherShadowIndex])) { continue; }

                ref readonly Shadow otherShadow = ref validShadowBuffer[otherShadowIndex];

                PooledLinkedListNode clipNode = shadowClippingOccluders.First;
                if (clipNode.IsNull) { break; }

                do
                {
                    PooledLinkedListNode nextClipNode = shadowClippingOccluders.Next(clipNode);
                    ref readonly Segment occluder = ref shadowClippingOccluders.ValueRef(clipNode);
                    // Clips the occluder against every shadows, replacing it with the resulting clipped segments.
                    // The reference above must not outlive this block: the insertion below can grow the backing
                    // array of the list and invalidate it.
                    int clipCount = occluder.ClipFrom(otherShadow, clipBuffer);
                    if (clipCount != 1 || occluder != clipBuffer[0])
                    {
                        for (int clipIndex = 0; clipIndex < clipCount; clipIndex++)
                        {
                            shadowClippingOccluders.AddBefore(clipNode, clipBuffer[clipIndex]);
                        }
                        shadowClippingOccluders.Remove(clipNode);
                    }
                    clipNode = nextClipNode;
                } while (clipNode.IsValid);
            }

            // Re-add if not fully occluded
            if (shadowClippingOccluders.Count > 0)
            {
                // Reinserts the shadow node back into the linked list at the appropriate position.
                if (previousShadowNode.IsValid)
                {
                    shadowIndexLinkedList.AttachAfter(previousShadowNode, currentShadowNode);
                }
                else
                {
                    shadowIndexLinkedList.AttachFirst(currentShadowNode);
                }
            }
            else
            {
                shadowIndexLinkedList.Recycle(currentShadowNode);
            }

            shadowClippingOccluders.Clear();
            currentShadowNode = previousShadowNode;
        }

        sortedShadowIndices.Clear();
        // Not AddRange: PooledLinkedList does not implement ICollection<T>, so List<T>.AddRange would fall back to
        // the IEnumerable<T> path and box the struct enumerator on every frame. The foreach below stays value-typed.
        foreach (int shadowIndex in shadowIndexLinkedList)
        {
            sortedShadowIndices.Add(shadowIndex);
        }
    }

    /// <summary>Applies shadow prediction based on view movement to avoid pop-in effects.</summary>
    private static void ApplyShadowPrediction(in Vector2 viewTargetPosition, in Vector2 viewDirection)
    {
        // With the pass running every update the margin would be a single frame wide, and shrinking the occluders
        // also makes it cull less; the geometry of the frame itself is the better trade.
        if (CullingInterval <= Timing.Step) { return; }
        if (viewDirection.LengthSquared() <= 0.01f) { return; }

        Vector2 predictedPosition = viewTargetPosition + viewDirection;

        // Identifies which occluders are likely to move based on view direction.
        predictableOccluderStart.Clear();
        predictableOccluderEnd.Clear();
        Span<int> shadowIndices = CollectionsMarshal.AsSpan(sortedShadowIndices);
        foreach (int currentShadowIndex in shadowIndices)
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

            // Half the occluder's length bounds the distance from its center to any of its points, and is measured
            // before the shortening below so it covers the modified occluders too.
            float reach = PredictionNeighborDistance + PredictionNeighborSlack + currentOccluder.Length * 0.5f;
            occluderNeighborReachSquared[currentShadowIndex] = reach * reach;
        }

        foreach (int currentShadowIndex in shadowIndices)
        {
            ref Shadow currentShadow = ref validShadowBuffer[currentShadowIndex];
            ref Segment currentOccluder = ref currentShadow.Occluder;

            // Applies prediction to the start point of an occluder.
            if (predictableOccluderStart.Contains(currentShadowIndex))
            {
                foreach (int otherShadowIndex in shadowIndices)
                {
                    if (currentShadowIndex == otherShadowIndex) { continue; }

                    ref readonly Shadow otherShadow = ref validShadowBuffer[otherShadowIndex];
                    ref readonly Segment otherOccluder = ref otherShadow.Occluder;

                    // Checks if start point prediction is valid for this occluder.
                    if (Vector2.DistanceSquared(otherOccluder.Center, currentOccluder.Start) > occluderNeighborReachSquared[otherShadowIndex]
                        || otherOccluder.ToPointDistanceSquared(currentOccluder.Start) >= PredictionNeighborDistanceSquared)
                    {
                        continue;
                    }

                    bool isOtherStartCloseEnough = (otherOccluder.Start - currentOccluder.Start).LengthSquared() < PredictionNeighborDistanceSquared;
                    bool isOtherEndCloseEnough = (otherOccluder.End - currentOccluder.Start).LengthSquared() < PredictionNeighborDistanceSquared;

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
                foreach (int otherShadowIndex in shadowIndices)
                {
                    if (currentShadowIndex == otherShadowIndex) { continue; }

                    ref readonly Shadow otherShadow = ref validShadowBuffer[otherShadowIndex];
                    ref readonly Segment otherOccluder = ref otherShadow.Occluder;

                    // Checks if end point prediction is valid for this occluder.
                    if (Vector2.DistanceSquared(otherOccluder.Center, currentOccluder.End) > occluderNeighborReachSquared[otherShadowIndex]
                        || otherOccluder.ToPointDistanceSquared(currentOccluder.End) >= PredictionNeighborDistanceSquared)
                    {
                        continue;
                    }

                    bool isOtherStartCloseEnough = (otherOccluder.Start - currentOccluder.End).LengthSquared() < PredictionNeighborDistanceSquared;
                    bool isOtherEndCloseEnough = (otherOccluder.End - currentOccluder.End).LengthSquared() < PredictionNeighborDistanceSquared;

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

    /// <summary>Performs culling on all entities and returns the count of culled entities.</summary>
    private static void CullEntities(Camera camera)
    {
        totalHullCulled = 0;
        totalNonHullCulled = 0;

        isEntityCulled.InvalidateAll();

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

        if (hullsForCulling.Count > ParallelTolerance)
        {
            Partitioner.Create(0, hullsForCulling.Count, PartitionRangeSize)
                .AsParallel()
                .WithDegreeOfParallelism(ParallelismLevel)
                .ForAll(CullHulls);
        }
        else
        {
            Cull(hullsForCulling, 0, hullsForCulling.Count, ref totalHullCulled);
        }

        entitiesForCulling.Clear();
        entitiesForCulling.AddRange(Submarine.visibleEntities);

        if (entitiesForCulling.Count > ParallelTolerance)
        {
            Partitioner.Create(0, entitiesForCulling.Count, PartitionRangeSize)
                .AsParallel()
                .WithDegreeOfParallelism(ParallelismLevel)
                .ForAll(CullOtherEntities);
        }
        else
        {
            Cull(entitiesForCulling, 0, entitiesForCulling.Count, ref totalNonHullCulled);
        }

        // Not AddRange: the LINQ Where() would allocate an iterator on every cull.
        charactersForCulling.Clear();
        foreach (Character character in Character.CharacterList)
        {
            if (character.IsVisible)
            {
                charactersForCulling.Add(character);
            }
        }

        Cull(charactersForCulling, 0, charactersForCulling.Count, ref totalNonHullCulled);
    }

    private static int totalHullCulled;
    private static int totalNonHullCulled;

    private static Action<Tuple<int, int>> CullHulls = static range => Cull(hullsForCulling, range.Item1, range.Item2, ref totalHullCulled);
    private static Action<Tuple<int, int>> CullOtherEntities = static range => Cull(entitiesForCulling, range.Item1, range.Item2, ref totalNonHullCulled);

    /// <summary>Culls a batch of entities.</summary>
    private static void Cull<T>(List<T> entities, int fromInclusive, int toExclusive, ref int totalCulled) where T : Entity
    {
        Span<Segment> entityEdges = stackalloc Segment[8];
        Span<Segment> edgeClipBuffer = stackalloc Segment[3];
        Span<int> shadowIndices = CollectionsMarshal.AsSpan(sortedShadowIndices);
        // The lease returns the list to the pool when this method exits, including on early returns and exceptions.
        using var clippingEdgesLease = segmentListPool.RentLease(out PooledLinkedList<Segment> clippingEdges);
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
            else if (typeof(T) == typeof(Character) && entity is Character { IsLocalPlayer: false } character)
            {
                entityAABB = EntityBounds.CalculateDynamic(character);
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
                    if (item.CurrentHull is Hull itemHull && isEntityCulled.GetValue(itemHull))
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

                    entityAABB = entityVisibleExtents.GetValue(structure);
                    entityAABB.Offset(structure.DrawPosition);

                    Hull? structureHull = entityHull.GetOrAdd(structure, static s => Hull.FindHull(s.WorldPosition));

                    if (structureHull != null && isEntityCulled.GetValue(structureHull))
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
            for (int quadrantIndex = 0; quadrantIndex < quadrants.Length; quadrantIndex++)
            {
                ref readonly QuadrantRayRange quadrant = ref quadrants[quadrantIndex];
                for (int edgeIndex = 4; edgeIndex < 8; edgeIndex++)
                {
                    ref readonly Segment edge = ref entityEdges[edgeIndex];
                    if (edge.IntersectWith(quadrant.Range))
                    {
                        if (++numCoveredQuadrants > 2) { goto SKIP; }
                        entityQuadrant |= quadrant.Quadrant;
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

                foreach (int shadowIndex in shadowIndices)
                {
                    if (!entityQuadrant.HasAnyFlag(shadowQuadrants[shadowIndex])) { continue; }

                    ref readonly Shadow shadow = ref validShadowBuffer[shadowIndex];
                    PooledLinkedListNode clipNode = clippingEdges.First;
                    if (clipNode.IsNull) { break; }

                    do
                    {
                        PooledLinkedListNode nextClipNode = clippingEdges.Next(clipNode);
                        ref readonly Segment edge = ref clippingEdges.ValueRef(clipNode);
                        // The reference above must not outlive this block: the insertion below can grow the backing
                        // array of the list and invalidate it.
                        int clipCount = edge.ClipFrom(shadow, edgeClipBuffer);
                        if (clipCount != 1 || edge != edgeClipBuffer[0])
                        {
                            for (int clipIndex = 0; clipIndex < clipCount; clipIndex++)
                            {
                                clippingEdges.AddBefore(clipNode, edgeClipBuffer[clipIndex]);
                            }
                            clippingEdges.Remove(clipNode);
                        }
                        clipNode = nextClipNode;
                    } while (clipNode.IsValid);

                    // Nothing left of this edge to clip, so the shadows behind it cannot change that.
                    if (clippingEdges.Count == 0) { break; }
                }

                bool refuseCulling = clippingEdges.Count > 0;

                clippingEdges.Clear();

                if (refuseCulling)
                {
                    goto SKIP;
                }
            }
        CULL:;
            isEntityCulled.SetValue(entity, true);
            entitiesCulled++;

        SKIP:;
        }

        Interlocked.Add(ref totalCulled, entitiesCulled);
    }
}
