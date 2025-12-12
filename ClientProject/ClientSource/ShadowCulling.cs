using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Lights;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;


namespace Whosyouradddy.ShadowCulling
{
    public partial class Plugin : IAssemblyPlugin
    {
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
                    CullEntities();
                }
            }
        }

        [HarmonyPatch(
            declaringType: typeof(GUI),
            methodName: nameof(GUI.Draw)
        )]
        class GUI_Draw
        {
            static void Postfix()
            {
                if (DebugDrawAABB)
                {
                    if (GameMain.spriteBatch != null
                        && !SubEditorScreen.IsSubEditor()
                        && Character.Controlled is Character character
                        && Screen.Selected?.Cam is Camera cam
                        && (GameMain.IsSingleplayer
                            ? GameMain.GameSession != null && GameMain.GameSession.IsRunning
                            : GameMain.Client?.GameStarted == true))
                    {
                        foreach (var mapEntity in Submarine.VisibleEntities)
                        {
                            if (mapEntity is not Item item
                                || item.IsHidden
                                || item.GetComponent<Wire>() is { Drawable: true }
                                || item.GetComponent<Ladder>() is not null)
                            {
                                continue;
                            }

                            // Draw a simple AABB of the item
                            RectangleF boundingBox = item.GetTransformedQuad().BoundingAxisAlignedRectangle;
                            Vector2 min = new Vector2(-boundingBox.Width / 2, -boundingBox.Height / 2);
                            Vector2 max = -min;
                            Rectangle extents = new(min.ToPoint(), (max - min).ToPoint());
                            extents.Offset(item.DrawPosition);
                            GUI.DrawRectangle(
                                GameMain.spriteBatch,
                                new Vector2[]
                                {
                                    cam.WorldToScreen(new Vector2(extents.Left, extents.Top)),
                                    cam.WorldToScreen(new Vector2(extents.Right, extents.Top)),
                                    cam.WorldToScreen(new Vector2(extents.Right, extents.Bottom)),
                                    cam.WorldToScreen(new Vector2(extents.Left, extents.Bottom)),
                                },
                                item.Visible ? Color.LightBlue : new(Color.LightBlue, 0.5f),
                                depth: 0.0004f,
                                thickness: 2.0f
                            );

                            // Draw AABB of cached extents
                            if (item.cachedVisibleExtents is Rectangle cachedExtents)
                            {
                                cachedExtents.Offset(item.DrawPosition);

                                GUI.DrawRectangle(
                                    GameMain.spriteBatch,
                                    new Vector2[]
                                    {
                                        cam.WorldToScreen(new Vector2(cachedExtents.X, cachedExtents.Y)),
                                        cam.WorldToScreen(new Vector2(cachedExtents.X + cachedExtents.Width * 2, cachedExtents.Y)),
                                        cam.WorldToScreen(new Vector2(cachedExtents.X + cachedExtents.Width * 2, cachedExtents.Y + cachedExtents.Height * 2)),
                                        cam.WorldToScreen(new Vector2(cachedExtents.X, cachedExtents.Y + cachedExtents.Height * 2)),
                                    },
                                    item.Visible ? Color.LightYellow : new(Color.LightYellow, 0.2f),
                                    depth: 0.0005f,
                                    thickness: 3.0f
                                );
                            }
                        }
                    }
                }
            }
        }

        private const int SampleNumber = 5;
        private static int ItemsPerBatch = 75;
        private const double CullInterval = 0.05;
        private static double prevCullTime;
        private static double prevShowPerf;
        private static bool dirtyCulling = false;

        private const int CacheSize = 8192;
        private static ConvexHull[] convexHullCache = new ConvexHull[CacheSize];
        private static LinkedList<(ConvexHull Hull, Rectangle AABB)> preFilteredConvexHulls = new();
        private static Rectangle[] convexHullAABBCache = new Rectangle[CacheSize];
        private static Rectangle[] convexHullShadowAABBCache = new Rectangle[CacheSize];
        private static ShadowVectors[] convexHullShadowVectorsCache = new ShadowVectors[CacheSize];
        private static List<Item> itemsToProcess = new(10000);

        private static readonly object counterLock = new();

        public static void CullEntities()
        {
            if (!GameMain.LightManager.LosEnabled
                || GameMain.LightManager.LosMode == LosMode.None
                || LightManager.ViewTarget is not Entity viewTarget
                || Screen.Selected?.Cam is not Camera camera)
            {
                if (dirtyCulling)
                {
                    Item.ItemList.ForEach(item =>
                    {
                        item.Visible = true;
                    });
                    dirtyCulling = false;
                }
                return;
            }

            if (prevCullTime > Timing.TotalTime - CullInterval) { return; }

            var stopWatch = new System.Diagnostics.Stopwatch();
            stopWatch.Start();

            Span<Vector2> samplePoints = stackalloc Vector2[SampleNumber];
            Span<bool> isInShadowCache = stackalloc bool[SampleNumber];
            preFilteredConvexHulls.Clear();

            Rectangle viewRect = camera.WorldView;

            // Get the convex hulls that intersect with the camera viewport
            foreach (ConvexHullList chList in ConvexHull.HullLists)
            {
                foreach (ConvexHull hull in chList.List)
                {
                    if (hull.IsInvalid || !hull.Enabled || hull.ShadowVertexCount < 6) { continue; }

                    Rectangle chAABB = hull.BoundingBox;
                    // In the world coordinate, the origin of the ConvexHull.BoundingBox is assumed to be left-bottom corner(perhaps due to historical reasons?)
                    // Here, it is necessary to convert its origin to the top-left corner to maintain consistency with other world rectangles.
                    chAABB.Y += chAABB.Height;

                    if (hull.ParentEntity?.Submarine is Submarine submarine)
                    {
                        chAABB.X += (int)submarine.DrawPosition.X;
                        chAABB.Y += (int)submarine.DrawPosition.Y;
                    }

                    if (chAABB.X > viewRect.X + viewRect.Width) { continue; }
                    if (chAABB.X + chAABB.Width < viewRect.X) { continue; }
                    if (chAABB.Y < viewRect.Y - viewRect.Height) { continue; }
                    if (chAABB.Y - chAABB.Height > viewRect.Y) { continue; }

                    preFilteredConvexHulls.AddLast((hull, chAABB));
                }
            }

            // Exclude the convex hulls whose sample points are all in shadow
            int length = 0;
            LinkedListNode<(ConvexHull Hull, Rectangle AABB)>? currentNode = preFilteredConvexHulls.First;
            while (currentNode != null)
            {
                var nextNode = currentNode.Next;
                ConvexHull current = currentNode.Value.Hull;
                Rectangle chAABB = currentNode.Value.AABB;

                // center
                samplePoints[0].X = chAABB.X + chAABB.Width / 2;
                samplePoints[0].Y = chAABB.Y - chAABB.Height / 2;
                // left top
                samplePoints[1].X = chAABB.X;
                samplePoints[1].Y = chAABB.Y;
                // right top
                samplePoints[2].X = chAABB.X + chAABB.Width;
                samplePoints[2].Y = chAABB.Y;
                // right bottom
                samplePoints[3].X = samplePoints[2].X;
                samplePoints[3].Y = chAABB.Y - chAABB.Height;
                // left bottom
                samplePoints[4].X = chAABB.X;
                samplePoints[4].Y = samplePoints[3].Y;

                int numSamplesInShadow = 0;
                isInShadowCache.Fill(false);

                LinkedListNode<(ConvexHull Hull, Rectangle)>? node = preFilteredConvexHulls.First;
                while (node != null)
                {
                    if (node == currentNode) { goto NEXT_NODE; }

                    ConvexHull hull = node.Value.Hull;

                    for (int i = 0; i < SampleNumber; i++)
                    {
                        if (isInShadowCache[i]) { continue; }

                        // Ray casting test
                        ref readonly Vector3 vertex_0 = ref hull.ShadowVertices[0].Position; // vertexPos1
                        ref readonly Vector3 vertex_1 = ref hull.ShadowVertices[1].Position; // vertexPos0
                        ref readonly Vector3 vertex_4 = ref hull.ShadowVertices[4].Position; // extruded0
                        ref readonly Vector3 vertex_5 = ref hull.ShadowVertices[5].Position; // extruded1

                        if ((vertex_1.Y > samplePoints[i].Y) != (vertex_0.Y > samplePoints[i].Y)
                                && samplePoints[i].X < (vertex_0.X - vertex_1.X) * (samplePoints[i].Y - vertex_1.Y)
                                                        / (vertex_0.Y - vertex_1.Y) + vertex_1.X)
                        {
                            isInShadowCache[i] = !isInShadowCache[i];
                        }

                        if ((vertex_4.Y > samplePoints[i].Y) != (vertex_1.Y > samplePoints[i].Y)
                                && samplePoints[i].X < (vertex_1.X - vertex_4.X) * (samplePoints[i].Y - vertex_4.Y)
                                                        / (vertex_1.Y - vertex_4.Y) + vertex_4.X)
                        {
                            isInShadowCache[i] = !isInShadowCache[i];
                        }

                        if ((vertex_5.Y > samplePoints[i].Y) != (vertex_4.Y > samplePoints[i].Y)
                                && samplePoints[i].X < (vertex_4.X - vertex_5.X) * (samplePoints[i].Y - vertex_5.Y)
                                                        / (vertex_4.Y - vertex_5.Y) + vertex_5.X)
                        {
                            isInShadowCache[i] = !isInShadowCache[i];
                        }

                        if ((vertex_0.Y > samplePoints[i].Y) != (vertex_5.Y > samplePoints[i].Y)
                                && samplePoints[i].X < (vertex_5.X - vertex_0.X) * (samplePoints[i].Y - vertex_0.Y)
                                                        / (vertex_5.Y - vertex_0.Y) + vertex_0.X)
                        {
                            isInShadowCache[i] = !isInShadowCache[i];
                        }

                        if (isInShadowCache[i])
                        {
                            numSamplesInShadow++;
                            if (numSamplesInShadow == SampleNumber)
                            {
                                preFilteredConvexHulls.Remove(currentNode);
                                goto ALL_SAMPLES_IN_SHADOW;
                            }
                        }
                    }
                NEXT_NODE:
                    node = node.Next;
                }

                convexHullCache[length++] = current;
            ALL_SAMPLES_IN_SHADOW:;

                currentNode = nextNode;
            }

            for (int ch_index = 0; ch_index < length; ch_index++)
            {
                ConvexHull hull = convexHullCache[ch_index];
                VertexPositionColor[] vertices = hull.ShadowVertices;
                ref readonly Vector3 vertex_0 = ref vertices[0].Position;
                ref readonly Vector3 vertex_1 = ref vertices[1].Position;
                ref readonly Vector3 vertex_4 = ref vertices[4].Position;
                ref readonly Vector3 vertex_5 = ref vertices[5].Position;

                // Cache data for the upcoming vector cross product directionality detection
                // true if ShadowVertices is not reversed
                if ((vertex_1 - vertex_0).LengthSquared() < (vertex_5 - vertex_4).LengthSquared())
                {
                    convexHullShadowVectorsCache[ch_index] = new ShadowVectors
                    {
                        V1_X = vertex_0.X - vertex_5.X,
                        V1_Y = vertex_0.Y - vertex_5.Y,
                        V1_Start_X = vertex_5.X,
                        V1_Start_Y = vertex_5.Y,

                        V2_X = vertex_1.X - vertex_0.X,
                        V2_Y = vertex_1.Y - vertex_0.Y,
                        V2_Start_X = vertex_0.X,
                        V2_Start_Y = vertex_0.Y,

                        V3_X = vertex_4.X - vertex_1.X,
                        V3_Y = vertex_4.Y - vertex_1.Y,
                        V3_Start_X = vertex_1.X,
                        V3_Start_Y = vertex_1.Y,
                    };
                }
                else
                {
                    convexHullShadowVectorsCache[ch_index] = new ShadowVectors
                    {
                        V1_X = vertex_5.X - vertex_0.X,
                        V1_Y = vertex_5.Y - vertex_0.Y,
                        V1_Start_X = vertex_0.X,
                        V1_Start_Y = vertex_0.Y,

                        V2_X = vertex_4.X - vertex_5.X,
                        V2_Y = vertex_4.Y - vertex_5.Y,
                        V2_Start_X = vertex_5.X,
                        V2_Start_Y = vertex_5.Y,

                        V3_X = vertex_1.X - vertex_4.X,
                        V3_Y = vertex_1.Y - vertex_4.Y,
                        V3_Start_X = vertex_4.X,
                        V3_Start_Y = vertex_4.Y,
                    };
                }

                // Cache the AABB of the convex hull's shadow
                float minX = MathF.Min(MathF.Min(vertex_0.X, vertex_1.X), MathF.Min(vertex_4.X, vertex_5.X));
                float maxX = MathF.Max(MathF.Max(vertex_0.X, vertex_1.X), MathF.Max(vertex_4.X, vertex_5.X));
                float minY = MathF.Min(MathF.Min(vertex_0.Y, vertex_1.Y), MathF.Min(vertex_4.Y, vertex_5.Y));
                float maxY = MathF.Max(MathF.Max(vertex_0.Y, vertex_1.Y), MathF.Max(vertex_4.Y, vertex_5.Y));
                convexHullShadowAABBCache[ch_index] = new Rectangle((int)minX, (int)minY, Math.Abs((int)maxX - (int)minX), Math.Abs((int)maxY - (int)minY));
            }

            itemsToProcess.Clear();

            foreach (var mapEntity in Submarine.VisibleEntities)
            {
                if (mapEntity is not Item item
                    || item.IsHidden
                    || !item.cachedVisibleExtents.HasValue
                    || item.GetComponent<Ladder>() is not null)
                {
                    continue;
                }

                if (item.GetComponent<Wire>() is { Drawable: true })
                {
                    item.Visible = true;
                    continue;
                }

                itemsToProcess.Add(item);
            }

            // stopWatch.Stop();
            int processedCount = 0;
            Parallel.For(
                fromInclusive: 0,
                toExclusive: (itemsToProcess.Count + ItemsPerBatch - 1) / ItemsPerBatch,
                parallelOptions: new ParallelOptions { MaxDegreeOfParallelism = 8 },
                body: BatchProcessing
            );

            void BatchProcessing(int index)
            {
                int start = index * ItemsPerBatch;
                int end = Math.Min(start + ItemsPerBatch, itemsToProcess.Count);

                Span<Vector2> _samplePoints = stackalloc Vector2[SampleNumber];
                Span<bool> _isInShadowCache = stackalloc bool[SampleNumber];

                for (int itemIndex = start; itemIndex < end; itemIndex++)
                {
                    Item item = itemsToProcess[itemIndex];
                    Rectangle itemAABB = item.cachedVisibleExtents!.Value;

                    // center
                    _samplePoints[0].X = item.DrawPosition.X;
                    _samplePoints[0].Y = item.DrawPosition.Y;
                    itemAABB.Offset(_samplePoints[0].X, _samplePoints[0].Y);

                    // In Vanilla, the AABB bounds calculation is messed up:
                    // ClientSource/Items/Item.cs | cachedVisibleExtents = extents = new Rectangle(min.ToPoint(), max.ToPoint());
                    // The correct one should be:
                    // cachedVisibleExtents = extents = new Rectangle(min.ToPoint(), (max - min).ToPoint());
                    // I have to calculate the sample points based on original cached extents in real-time here:
                    itemAABB.Width *= 2;
                    itemAABB.Height *= 2;

                    // left bottom
                    _samplePoints[1].X = itemAABB.X;
                    _samplePoints[1].Y = itemAABB.Y;
                    // right bottom
                    _samplePoints[2].X = itemAABB.X + itemAABB.Width;
                    _samplePoints[2].Y = itemAABB.Y;
                    // right top
                    _samplePoints[3].X = _samplePoints[2].X;
                    _samplePoints[3].Y = itemAABB.Y + itemAABB.Height;
                    // left top
                    _samplePoints[4].X = itemAABB.X;
                    _samplePoints[4].Y = _samplePoints[3].Y;

                    // Skip if there is no intersection
                    if (_samplePoints[1].Y > viewRect.Y
                        || _samplePoints[3].X < viewRect.X
                        || _samplePoints[1].X > viewRect.X + viewRect.Width
                        || _samplePoints[3].Y < viewRect.Y - viewRect.Height)
                    {
                        continue;
                    }

                    int numSamplesInShadow = 0;
                    _isInShadowCache.Fill(false);

                    for (int ch_index = 0; ch_index < length; ch_index++)
                    {
                        ConvexHull hull = convexHullCache[ch_index];
                        itemAABB.Intersects(ref convexHullShadowAABBCache[ch_index], out bool intersecting);
                        if (!intersecting) { continue; }

                        for (int i = 0; i < SampleNumber; i++)
                        {
                            if (_isInShadowCache[i]) { continue; }

                            // Using the vector cross product for directional detection,
                            // if the vectors from the shadow vertex to each sample point are all on the same side of the shadow vector,
                            // then the sample point is within the shadow range.
                            ref readonly ShadowVectors vectors = ref convexHullShadowVectorsCache[ch_index];
                            // We prioritize detecting the vector (V2) on the shadow vertices rather than their extruded points.
                            if ((_samplePoints[i].X - vectors.V2_Start_X) * vectors.V2_Y < (_samplePoints[i].Y - vectors.V2_Start_Y) * vectors.V2_X) { continue; }
                            if ((_samplePoints[i].X - vectors.V1_Start_X) * vectors.V1_Y < (_samplePoints[i].Y - vectors.V1_Start_Y) * vectors.V1_X) { continue; }
                            if ((_samplePoints[i].X - vectors.V3_Start_X) * vectors.V3_Y < (_samplePoints[i].Y - vectors.V3_Start_Y) * vectors.V3_X) { continue; }

                            if (++numSamplesInShadow == SampleNumber)
                            {
                                item.Visible = false;
                                goto ALL_SAMPLES_IN_SHADOW;
                            }

                            _isInShadowCache[i] = true;
                        }
                    }

                    item.Visible = true;
                ALL_SAMPLES_IN_SHADOW:

                    lock (counterLock)
                    {
                        processedCount++;
                    }
                }
            }

            stopWatch.Stop();
            GameMain.PerformanceCounter.AddElapsedTicks("Mod:ShadowCulling", stopWatch.ElapsedTicks);

            if (DebugLog && Timing.TotalTime - prevShowPerf >= 2.0f)
            {
                float average = GameMain.PerformanceCounter.GetAverageElapsedMillisecs("Mod:ShadowCulling");
                LuaCsLogger.LogMessage($"Mod:ShadowCulling | Mean: {average} | Cull: {processedCount}/{Submarine.VisibleEntities.Count()} | Hulls: {length}");
                prevShowPerf = Timing.TotalTime;
            }

            prevCullTime = Timing.TotalTime;

            dirtyCulling = true;

        }

        // V1: extruded1 => vertexPos1
        // V2: vertexPos1 => vertexPos0
        // V3: vertexPos0 => extruded0
        public readonly record struct ShadowVectors(
            float V1_X, float V1_Y, float V1_Start_X, float V1_Start_Y,
            float V2_X, float V2_Y, float V2_Start_X, float V2_Start_Y,
            float V3_X, float V3_Y, float V3_Start_X, float V3_Start_Y);

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