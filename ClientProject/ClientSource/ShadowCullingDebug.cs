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
            declaringType: typeof(GUI),
            methodName: nameof(GUI.Draw)
        )]
        class GUI_Draw
        {
            static void Postfix()
            {
                if (DebugDraw)
                {
                    if (GameMain.spriteBatch is SpriteBatch spriteBatch
                        && !SubEditorScreen.IsSubEditor()
                        && Character.Controlled is Character character
                        && Screen.Selected?.Cam is Camera cam
                        && (GameMain.IsSingleplayer
                            ? GameMain.GameSession != null && GameMain.GameSession.IsRunning
                            : GameMain.Client?.GameStarted == true))
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
                                cam.WorldToScreen(shadow.Ray1.Origin + shadow.Ray1.Direction * 300.0f),
                                Color.BlueViolet,
                                width: 1
                            );
                            GUI.DrawLine(
                                spriteBatch,
                                cam.WorldToScreen(shadow.Ray2.Origin),
                                cam.WorldToScreen(shadow.Ray2.Origin + shadow.Ray2.Direction * 300.0f),
                                Color.BlueViolet,
                                width: 1
                            );
                        }

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
                                depth: 0.04f,
                                thickness: 2.0f
                            );

                            // Draw AABB of cached extents
                            if (item.cachedVisibleExtents is Rectangle itemCachedExtents)
                            {
                                itemCachedExtents.Offset(item.DrawPosition);

                                GUI.DrawRectangle(
                                    GameMain.spriteBatch,
                                    new Vector2[]
                                    {
                                        cam.WorldToScreen(new Vector2(itemCachedExtents.X, itemCachedExtents.Y)),
                                        cam.WorldToScreen(new Vector2(itemCachedExtents.X + itemCachedExtents.Width * 2, itemCachedExtents.Y)),
                                        cam.WorldToScreen(new Vector2(itemCachedExtents.X + itemCachedExtents.Width * 2, itemCachedExtents.Y + itemCachedExtents.Height * 2)),
                                        cam.WorldToScreen(new Vector2(itemCachedExtents.X, itemCachedExtents.Y + itemCachedExtents.Height * 2)),
                                    },
                                    item.Visible ? Color.LightYellow : new(Color.LightYellow, 0.2f),
                                    depth: 0.05f,
                                    thickness: 3.0f
                                );
                            }
                        }
                    }
                }
            }
        }
    }
}