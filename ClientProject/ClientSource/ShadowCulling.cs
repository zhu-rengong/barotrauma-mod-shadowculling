using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Runtime.CompilerServices;
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

        private static List<ConvexHull> convexHulls = new(100);
        private static Dictionary<ConvexHull, Rectangle> chAABBCache = new(100);
        private static ConcurrentDictionary<ConvexHull, Rectangle> chShadowAABBCache = new();
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

            convexHulls.Clear();
            chAABBCache.Clear();
            chShadowAABBCache.Clear();
            Rectangle viewRect = camera.WorldView;

            // Get the convex hulls that intersect with the camera viewport
            foreach (ConvexHullList chList in ConvexHull.HullLists)
            {
                foreach (ConvexHull hull in chList.List)
                {
                    if (hull.IsInvalid || !hull.Enabled) { continue; }

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

                    convexHulls.Add(hull);

                    // Cache the AABB of the convex hull
                    chAABBCache[hull] = chAABB;

                    // Cache the AABB of the convex hull's shadow
                    VertexPositionColor[] vertices = hull.ShadowVertices;
                    int vertexCount = hull.ShadowVertexCount;
                    float minX = float.MaxValue, minY = float.MaxValue;
                    float maxX = float.MinValue, maxY = float.MinValue;

                    for (int j = 0; j < vertexCount; j++)
                    {
                        ref readonly Vector3 pos = ref vertices[j].Position;
                        float posX = pos.X, posY = pos.Y;
                        if (posX < minX) { minX = posX; }
                        if (posX > maxX) { maxX = posX; }
                        if (posY < minY) { minY = posY; }
                        if (posY > maxY) { maxY = posY; }
                    }

                    chShadowAABBCache[hull] = new Rectangle((int)minX, (int)minY, Math.Abs((int)maxX - (int)minX), Math.Abs((int)maxY - (int)minY));
                }
            }

            // Exclude the convex hulls whose sample points are all in shadow
            for (int ch_index = convexHulls.Count - 1; ch_index >= 0; ch_index--)
            {
                ConvexHull current = convexHulls[ch_index];
                Rectangle chAABB = chAABBCache[current];

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

                foreach (var hull in convexHulls)
                {
                    if (!chAABB.Intersects(chShadowAABBCache[hull])) { continue; }

                    int vertexCount = hull.ShadowVertexCount;

                    for (int i = 0; i < SampleNumber; i++)
                    {
                        if (isInShadowCache[i]) { continue; }

                        // Ray casting test
                        for (int k = 0; k < vertexCount; k += 6)
                        {
                            ref readonly Vector3 shadowVertex1 = ref hull.ShadowVertices[k].Position;
                            ref readonly Vector3 shadowVertex2 = ref hull.ShadowVertices[k + 1].Position;
                            ref readonly Vector3 shadowVertex3 = ref hull.ShadowVertices[k + 4].Position;
                            ref readonly Vector3 shadowVertex4 = ref hull.ShadowVertices[k + 5].Position;

                            if ((shadowVertex2.Y > samplePoints[i].Y) != (shadowVertex1.Y > samplePoints[i].Y)
                                    && samplePoints[i].X < (shadowVertex1.X - shadowVertex2.X) * (samplePoints[i].Y - shadowVertex2.Y)
                                                                / (shadowVertex1.Y - shadowVertex2.Y) + shadowVertex2.X)
                            {
                                isInShadowCache[i] = !isInShadowCache[i];
                            }

                            if ((shadowVertex3.Y > samplePoints[i].Y) != (shadowVertex2.Y > samplePoints[i].Y)
                                    && samplePoints[i].X < (shadowVertex2.X - shadowVertex3.X) * (samplePoints[i].Y - shadowVertex3.Y)
                                                                / (shadowVertex2.Y - shadowVertex3.Y) + shadowVertex3.X)
                            {
                                isInShadowCache[i] = !isInShadowCache[i];
                            }

                            if ((shadowVertex4.Y > samplePoints[i].Y) != (shadowVertex3.Y > samplePoints[i].Y)
                                    && samplePoints[i].X < (shadowVertex3.X - shadowVertex4.X) * (samplePoints[i].Y - shadowVertex4.Y)
                                                                / (shadowVertex3.Y - shadowVertex4.Y) + shadowVertex4.X)
                            {
                                isInShadowCache[i] = !isInShadowCache[i];
                            }

                            if ((shadowVertex1.Y > samplePoints[i].Y) != (shadowVertex4.Y > samplePoints[i].Y)
                                    && samplePoints[i].X < (shadowVertex4.X - shadowVertex1.X) * (samplePoints[i].Y - shadowVertex1.Y)
                                                                / (shadowVertex4.Y - shadowVertex1.Y) + shadowVertex1.X)
                            {
                                isInShadowCache[i] = !isInShadowCache[i];
                            }

                            if (isInShadowCache[i])
                            {
                                numSamplesInShadow++;
                                if (numSamplesInShadow == SampleNumber)
                                {
                                    convexHulls.RemoveAt(ch_index);
                                    goto ALL_SAMPLES_IN_SHADOW;
                                }
                                break;
                            }
                        }
                    }
                }
            ALL_SAMPLES_IN_SHADOW:;
            }

            itemsToProcess.Clear();

            foreach (var mapEntity in Submarine.VisibleEntities)
            {
                if (mapEntity is not Item item
                    || item.IsHidden
                    || item.GetComponent<Ladder>() is not null
                    || item.cachedVisibleExtents is not Rectangle)
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

                    foreach (var hull in convexHulls)
                    {
                        if (!itemAABB.Intersects(chShadowAABBCache[hull])) { continue; }

                        int vertexCount = hull.ShadowVertexCount;

                        for (int i = 0; i < SampleNumber; i++)
                        {
                            if (_isInShadowCache[i]) { continue; }

                            for (int k = 0; k < vertexCount; k += 6)
                            {
                                ref readonly Vector3 shadowVertex1 = ref hull.ShadowVertices[k].Position;
                                ref readonly Vector3 shadowVertex2 = ref hull.ShadowVertices[k + 1].Position;
                                ref readonly Vector3 shadowVertex3 = ref hull.ShadowVertices[k + 4].Position;
                                ref readonly Vector3 shadowVertex4 = ref hull.ShadowVertices[k + 5].Position;

                                if ((shadowVertex2.Y > _samplePoints[i].Y) != (shadowVertex1.Y > _samplePoints[i].Y)
                                        && _samplePoints[i].X < (shadowVertex1.X - shadowVertex2.X) * (_samplePoints[i].Y - shadowVertex2.Y)
                                                                    / (shadowVertex1.Y - shadowVertex2.Y) + shadowVertex2.X)
                                {
                                    _isInShadowCache[i] = !_isInShadowCache[i];
                                }

                                if ((shadowVertex3.Y > _samplePoints[i].Y) != (shadowVertex2.Y > _samplePoints[i].Y)
                                        && _samplePoints[i].X < (shadowVertex2.X - shadowVertex3.X) * (_samplePoints[i].Y - shadowVertex3.Y)
                                                                    / (shadowVertex2.Y - shadowVertex3.Y) + shadowVertex3.X)
                                {
                                    _isInShadowCache[i] = !_isInShadowCache[i];
                                }

                                if ((shadowVertex4.Y > _samplePoints[i].Y) != (shadowVertex3.Y > _samplePoints[i].Y)
                                        && _samplePoints[i].X < (shadowVertex3.X - shadowVertex4.X) * (_samplePoints[i].Y - shadowVertex4.Y)
                                                                    / (shadowVertex3.Y - shadowVertex4.Y) + shadowVertex4.X)
                                {
                                    _isInShadowCache[i] = !_isInShadowCache[i];
                                }

                                if ((shadowVertex1.Y > _samplePoints[i].Y) != (shadowVertex4.Y > _samplePoints[i].Y)
                                        && _samplePoints[i].X < (shadowVertex4.X - shadowVertex1.X) * (_samplePoints[i].Y - shadowVertex1.Y)
                                                                    / (shadowVertex4.Y - shadowVertex1.Y) + shadowVertex1.X)
                                {
                                    _isInShadowCache[i] = !_isInShadowCache[i];
                                }

                                if (_isInShadowCache[i])
                                {
                                    numSamplesInShadow++;
                                    if (numSamplesInShadow == SampleNumber)
                                    {
                                        item.Visible = false;
                                        goto ALL_SAMPLES_IN_SHADOW;
                                    }
                                    break;
                                }
                            }
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
                LuaCsLogger.LogMessage($"Mod:ShadowCulling | Mean: {average} | Cull: {processedCount}/{Submarine.VisibleEntities.Count()} | Hulls: {convexHulls.Count}");
                prevShowPerf = Timing.TotalTime;
            }

            prevCullTime = Timing.TotalTime;

            dirtyCulling = true;
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