using System;
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

        private const double CullInterval = 0.05;

        private static double prevCullTime;
        private static double prevShowPerf;

        private static bool dirtyCulling = false;

        private static List<ConvexHull> convexHulls = new(64);
        private static Dictionary<ConvexHull, Rectangle> hullShadowAABB = new(64);

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
            int howManyPointsAreInShadow;

            convexHulls.Clear();
            hullShadowAABB.Clear();
            Rectangle viewRect = camera.WorldView;

            // Get the convex hulls that intersect with the camera viewport
            foreach (ConvexHullList chList in ConvexHull.HullLists)
            {
                foreach (ConvexHull hull in chList.List)
                {
                    if (hull.IsInvalid || !hull.Enabled) { continue; }

                    Rectangle hullAABB = hull.BoundingBox;
                    // In the world coordinate, the origin of the ConvexHull.BoundingBox is assumed to be left-bottom corner(perhaps due to historical reasons?)
                    // Here, it is necessary to convert its origin to the top-left corner to maintain consistency with other world rectangles.
                    hullAABB.Y += hullAABB.Height;

                    if (hull.ParentEntity?.Submarine is Submarine submarine)
                    {
                        hullAABB.X += (int)submarine.DrawPosition.X;
                        hullAABB.Y += (int)submarine.DrawPosition.Y;
                    }

                    if (hullAABB.X > viewRect.X + viewRect.Width) { continue; }
                    if (hullAABB.X + hullAABB.Width < viewRect.X) { continue; }
                    if (hullAABB.Y < viewRect.Y - viewRect.Height) { continue; }
                    if (hullAABB.Y - hullAABB.Height > viewRect.Y) { continue; }

                    convexHulls.Add(hull);
                }
            }

            // Exclude the convex hulls whose sample points are all in shadow
            for (int i = convexHulls.Count - 1; i >= 0; i--)
            {
                ConvexHull current = convexHulls[i];
                howManyPointsAreInShadow = 0;

                Rectangle hullAABB = current.BoundingBox;
                hullAABB.Y += hullAABB.Height;

                Vector2 offset = Vector2.Zero;
                if (current.ParentEntity?.Submarine is Submarine submarine)
                {
                    offset.X = submarine.DrawPosition.X;
                    offset.Y = submarine.DrawPosition.Y;
                }

                samplePoints[0].X = hullAABB.X + hullAABB.Width / 2 + offset.X;
                samplePoints[0].Y = hullAABB.Y - hullAABB.Height / 2 + offset.Y;
                SegmentPoint[] segmentPoints = current.vertices;
                samplePoints[1].X = segmentPoints[0].Pos.X + offset.X;
                samplePoints[1].Y = segmentPoints[0].Pos.Y + offset.Y;
                samplePoints[2].X = segmentPoints[1].Pos.X + offset.X;
                samplePoints[2].Y = segmentPoints[1].Pos.Y + offset.Y;
                samplePoints[3].X = segmentPoints[2].Pos.X + offset.X;
                samplePoints[3].Y = segmentPoints[2].Pos.Y + offset.Y;
                samplePoints[4].X = segmentPoints[3].Pos.X + offset.X;
                samplePoints[4].Y = segmentPoints[3].Pos.Y + offset.Y;

                for (int j = 0; j < SampleNumber; j++)
                {
                    ref readonly Vector2 point = ref samplePoints[j];
                    bool isInShadow = false;

                    foreach (var hull in convexHulls)
                    {
                        int vertexCount = hull.ShadowVertexCount;

                        for (int k = 0; k < vertexCount; k += 3)
                        {
                            ref readonly Vector3 shadowVertex1 = ref hull.ShadowVertices[k].Position;
                            ref readonly Vector3 shadowVertex2 = ref hull.ShadowVertices[k + 1].Position;
                            ref readonly Vector3 shadowVertex3 = ref hull.ShadowVertices[k + 2].Position;
                            Vector2 v0, v1, v2;

                            // Compute vectors
                            v0.X = shadowVertex3.X - shadowVertex1.X;
                            v0.Y = shadowVertex3.Y - shadowVertex1.Y;
                            v1.X = shadowVertex2.X - shadowVertex1.X;
                            v1.Y = shadowVertex2.Y - shadowVertex1.Y;
                            v2.X = point.X - shadowVertex1.X;
                            v2.Y = point.Y - shadowVertex1.Y;
                            // Compute dot products
                            float dot00 = v0.X * v0.X + v0.Y * v0.Y;
                            float dot01 = v0.X * v1.X + v0.Y * v1.Y;
                            float dot02 = v0.X * v2.X + v0.Y * v2.Y;
                            float dot11 = v1.X * v1.X + v1.Y * v1.Y;
                            float dot12 = v1.X * v2.X + v1.Y * v2.Y;
                            // Compute barycentric coordinates
                            float invDenom = 1.0f / (dot00 * dot11 - dot01 * dot01);
                            float u = (dot11 * dot02 - dot01 * dot12) * invDenom;
                            // Check if the point is in triangle
                            if (u < 0.0f) { continue; }
                            float v = (dot00 * dot12 - dot01 * dot02) * invDenom;
                            if (v >= 0.0f && (u + v) < 1.0f)
                            {
                                howManyPointsAreInShadow++;
                                isInShadow = true;
                                break;
                            }
                        }

                        if (isInShadow) { break; }
                    }
                }

                if (howManyPointsAreInShadow == SampleNumber)
                {
                    convexHulls.RemoveAt(i);
                }
                else
                {
                    // Cache the AABB of the convex hull's shadow
                    VertexPositionColor[] vertices = current.ShadowVertices;
                    int vertexCount = current.ShadowVertexCount;
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

                    hullShadowAABB.Add(current, new Rectangle((int)minX, (int)minY, Math.Abs((int)maxX - (int)minX), Math.Abs((int)maxY - (int)minY)));
                }
            }

            int count = 0;
            foreach (var mapEntity in Submarine.VisibleEntities)
            {
                if (mapEntity is not Item item
                    || item.IsHidden
                    || item.GetComponent<Ladder>() is not null
                    || item.cachedVisibleExtents is not Rectangle itemAABB)
                {
                    continue;
                }

                if (item.GetComponent<Wire>() is { Drawable: true })
                {
                    item.Visible = true;
                    continue;
                }

                samplePoints[0].X = item.DrawPosition.X;
                samplePoints[0].Y = item.DrawPosition.Y;
                itemAABB.Offset(samplePoints[0].X, samplePoints[0].Y);

                // In Vanilla, the AABB bounds calculation is incorrect:
                // ClientSource/Items/Item.cs | cachedVisibleExtents = extents = new Rectangle(min.ToPoint(), max.ToPoint());
                // The correct one should be:
                // cachedVisibleExtents = extents = new Rectangle(min.ToPoint(), (max - min).ToPoint());
                // I have to calculate the sample points based on incorrect cached extents in real-time here:
                // left bottom
                samplePoints[1].X = itemAABB.X;
                samplePoints[1].Y = itemAABB.Y;
                // right bottom
                samplePoints[2].X = itemAABB.X + itemAABB.Width * 2;
                samplePoints[2].Y = itemAABB.Y;
                // right top
                samplePoints[3].X = samplePoints[2].X;
                samplePoints[3].Y = itemAABB.Y + itemAABB.Height * 2;
                // left top
                samplePoints[4].X = itemAABB.X;
                samplePoints[4].Y = samplePoints[3].Y;

                // Skip if there is no intersection
                if (samplePoints[1].Y > viewRect.Y
                    || samplePoints[3].X < viewRect.X
                    || samplePoints[1].X > viewRect.X + viewRect.Width
                    || samplePoints[3].Y < viewRect.Y - viewRect.Height)
                {
                    continue;
                }

                /* TODO: use the following code to replace the above one when the issue is fixed
                samplePoints[1].X = boundingBox.X;
                samplePoints[1].Y = boundingBox.Y;
                samplePoints[2].X = boundingBox.X + boundingBox.Width * 2;
                samplePoints[2].Y = boundingBox.Y;
                samplePoints[3].X = boundingBox.X;
                samplePoints[3].Y = boundingBox.Y + boundingBox.Height * 2;
                samplePoints[4].X = samplePoints[2].X;
                samplePoints[4].Y = samplePoints[3].Y;
                */

                howManyPointsAreInShadow = 0;
                isInShadowCache.Fill(false);

                foreach (var hull in convexHulls)
                {
                    if (!itemAABB.Intersects(hullShadowAABB[hull])) { continue; }

                    for (int i = 0; i < SampleNumber; i++)
                    {
                        if (isInShadowCache[i]) { continue; }

                        int vertexCount = hull.ShadowVertexCount;

                        for (int j = 0; j < vertexCount; j += 3)
                        {
                            ref readonly Vector3 shadowVertex1 = ref hull.ShadowVertices[j].Position;
                            ref readonly Vector3 shadowVertex2 = ref hull.ShadowVertices[j + 1].Position;
                            ref readonly Vector3 shadowVertex3 = ref hull.ShadowVertices[j + 2].Position;
                            Vector2 v0, v1, v2;

                            // Compute vectors
                            v0.X = shadowVertex3.X - shadowVertex1.X;
                            v0.Y = shadowVertex3.Y - shadowVertex1.Y;
                            v1.X = shadowVertex2.X - shadowVertex1.X;
                            v1.Y = shadowVertex2.Y - shadowVertex1.Y;
                            v2.X = samplePoints[i].X - shadowVertex1.X;
                            v2.Y = samplePoints[i].Y - shadowVertex1.Y;
                            // Compute dot products
                            float dot00 = v0.X * v0.X + v0.Y * v0.Y;
                            float dot01 = v0.X * v1.X + v0.Y * v1.Y;
                            float dot02 = v0.X * v2.X + v0.Y * v2.Y;
                            float dot11 = v1.X * v1.X + v1.Y * v1.Y;
                            float dot12 = v1.X * v2.X + v1.Y * v2.Y;
                            // Compute barycentric coordinates
                            float invDenom = 1.0f / (dot00 * dot11 - dot01 * dot01);
                            float u = (dot11 * dot02 - dot01 * dot12) * invDenom;
                            // Check if the point is in triangle
                            if (u < 0.0f) { continue; }
                            float v = (dot00 * dot12 - dot01 * dot02) * invDenom;
                            if (v >= 0.0f && (u + v) < 1.0f)
                            {
                                howManyPointsAreInShadow++;
                                isInShadowCache[i] = true;
                                break;
                            }
                        }
                    }
                }

                item.Visible = howManyPointsAreInShadow < SampleNumber;

                count++;
            }

            stopWatch.Stop();
            GameMain.PerformanceCounter.AddElapsedTicks("Mod:ShadowCulling", stopWatch.ElapsedTicks);

            if (DebugLog && Timing.TotalTime - prevShowPerf >= 2.0f)
            {
                float avgTime = GameMain.PerformanceCounter.GetAverageElapsedMillisecs("Mod:ShadowCulling");
                LuaCsLogger.LogMessage($"Mod:ShadowCulling | Mean: {avgTime} | Cull: {count}/{Submarine.VisibleEntities.Count()} | Hulls: {convexHulls.Count}");
                prevShowPerf = Timing.TotalTime;
            }

            prevCullTime = Timing.TotalTime;

            dirtyCulling = true;
        }
    }
}