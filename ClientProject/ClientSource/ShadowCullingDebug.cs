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
        [HarmonyPatch(
            declaringType: typeof(GameScreen),
            methodName: nameof(GameScreen.DrawMap)
        )]
        class Submarine_DrawFront
        {
            static void Postfix(SpriteBatch spriteBatch)
            {
                if (DebugDraw)
                {
                    if (!SubEditorScreen.IsSubEditor()
                        && GameMain.GameScreen.Cam is Camera cam
                        && (GameMain.IsSingleplayer
                            ? GameMain.GameSession != null && GameMain.GameSession.IsRunning
                            : GameMain.Client?.GameStarted == true))
                    {
                        spriteBatch.Begin(SpriteSortMode.Deferred, null, GUI.SamplerState, null, GameMain.ScissorTestEnable);

                        if (DebugDrawHull)
                        {
                            foreach (Hull hull in hullsForCulling)
                            {
                                RectangleF worldRect = hull.WorldRect;
                                GUI.DrawRectangle(
                                    spriteBatch,
                                    new Vector2[]
                                    {
                                        cam.WorldToScreen(new(worldRect.X, worldRect.Y)),
                                        cam.WorldToScreen(new(worldRect.X + worldRect.Width, worldRect.Y)),
                                        cam.WorldToScreen(new(worldRect.X + worldRect.Width, worldRect.Y - worldRect.Height)),
                                        cam.WorldToScreen(new(worldRect.X, worldRect.Y - worldRect.Height)),
                                    },
                                    isEntityCulled.TryGetValue(hull, out bool _) ? new(Color.MediumPurple, 0.5f) : Color.MediumPurple,
                                    thickness: 4.0f
                                );
                            }
                        }

                        if (DebugDrawShadow && CullingEnabled)
                        {
                            foreach (int shadowIndex in sortedShadowIndices)
                            {
                                ref readonly Shadow shadow = ref validShadowBuffer[shadowIndex];
                                GUI.DrawLine(
                                    spriteBatch,
                                    cam.WorldToScreen(shadow.Occluder.Start),
                                    cam.WorldToScreen(shadow.Occluder.End),
                                    Color.BlueViolet,
                                    width: 3
                                );
                                GUI.DrawLine(
                                    spriteBatch,
                                    cam.WorldToScreen(shadow.Ray1.Origin),
                                    cam.WorldToScreen(shadow.Ray1.Origin + shadow.Ray1.Direction * DebugDrawShadowLength),
                                    Color.BlueViolet,
                                    width: 1
                                );
                                GUI.DrawLine(
                                    spriteBatch,
                                    cam.WorldToScreen(shadow.Ray2.Origin),
                                    cam.WorldToScreen(shadow.Ray2.Origin + shadow.Ray2.Direction * DebugDrawShadowLength),
                                    Color.BlueViolet,
                                    width: 1
                                );
                            }
                        }

                        foreach (var mapEntity in Submarine.VisibleEntities)
                        {
                            if (mapEntity.IsHidden) { continue; }

                            if (DebugDrawItem && mapEntity is Item item)
                            {
                                if (item.isWire)
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
                                    spriteBatch,
                                    new Vector2[]
                                    {
                                        cam.WorldToScreen(new(extents.Left, extents.Top)),
                                        cam.WorldToScreen(new(extents.Right, extents.Top)),
                                        cam.WorldToScreen(new(extents.Right, extents.Bottom)),
                                        cam.WorldToScreen(new(extents.Left, extents.Bottom)),
                                    },
                                    item.Visible ? Color.LightBlue : new(Color.LightBlue, 0.5f),
                                    thickness: 2.0f
                                );

                                // Draw AABB of cached extents
                                if (item.cachedVisibleExtents is Rectangle itemCachedExtents)
                                {
                                    itemCachedExtents.Offset(item.DrawPosition);

                                    GUI.DrawRectangle(
                                        spriteBatch,
                                        new Vector2[]
                                        {
                                            cam.WorldToScreen(new(itemCachedExtents.X, itemCachedExtents.Y)),
                                            cam.WorldToScreen(new(itemCachedExtents.X + itemCachedExtents.Width * 2, itemCachedExtents.Y)),
                                            cam.WorldToScreen(new(itemCachedExtents.X + itemCachedExtents.Width * 2, itemCachedExtents.Y + itemCachedExtents.Height * 2)),
                                            cam.WorldToScreen(new(itemCachedExtents.X, itemCachedExtents.Y + itemCachedExtents.Height * 2)),
                                        },
                                        isEntityCulled.TryGetValue(item, out bool _) ? new(Color.LightYellow, 0.2f) : Color.LightYellow,
                                        thickness: 3.0f
                                    );
                                }
                            }
                            else if (DebugDrawStructure && mapEntity is Structure structure)
                            {
                                if (structure.Prefab.DecorativeSprites.Length > 0)
                                {
                                    continue;
                                }

                                RectangleF worldRect = Quad2D.FromSubmarineRectangle(structure.WorldRect).Rotated(
                                         structure.FlippedX != structure.FlippedY
                                             ? structure.RotationRad
                                             : -structure.RotationRad).BoundingAxisAlignedRectangle;
                                worldRect.Y += worldRect.Height;
                                GUI.DrawRectangle(
                                    spriteBatch,
                                    new Vector2[]
                                    {
                                        cam.WorldToScreen(new(worldRect.X, worldRect.Y)),
                                        cam.WorldToScreen(new(worldRect.X + worldRect.Width, worldRect.Y)),
                                        cam.WorldToScreen(new(worldRect.X + worldRect.Width, worldRect.Y - worldRect.Height)),
                                        cam.WorldToScreen(new(worldRect.X, worldRect.Y - worldRect.Height)),
                                    },
                                    isEntityCulled.TryGetValue(structure, out bool _) ? new(Color.Green, 0.2f) : Color.Green,
                                    thickness: 2.0f
                                );
                            }
                        }
                    }

                    spriteBatch.End();
                }
            }
        }
    }
}