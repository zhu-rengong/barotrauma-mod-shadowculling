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
        class GameScreen_DrawMap
        {
            static void Postfix(SpriteBatch spriteBatch)
            {
                if (!DebugDraw || DisallowCulling || GameMain.GameScreen.Cam is not Camera cam) { return; }

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

                if (DebugDrawShadow)
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

                foreach (var entity in Submarine.VisibleEntities)
                {
                    if (entity.IsHidden) { continue; }

                    if (DebugDrawItem && entity is Item item)
                    {
                        if (!item.cachedVisibleExtents.HasValue || !item.Visible || item.isWire)
                        {
                            continue;
                        }

                        RectangleF entityAABB = item.cachedVisibleExtents.Value;
                        entityAABB.Width -= entityAABB.X;
                        entityAABB.Height -= entityAABB.Y;
                        entityAABB.Y += entityAABB.Height;
                        entityAABB.Offset(entity.DrawPosition);

                        GUI.DrawRectangle(
                            spriteBatch,
                            new Vector2[]
                            {
                                cam.WorldToScreen(new(entityAABB.X, entityAABB.Y)),
                                cam.WorldToScreen(new(entityAABB.X + entityAABB.Width, entityAABB.Y)),
                                cam.WorldToScreen(new(entityAABB.X + entityAABB.Width, entityAABB.Y - entityAABB.Height)),
                                cam.WorldToScreen(new(entityAABB.X, entityAABB.Y - entityAABB.Height)),
                            },
                            isEntityCulled.TryGetValue(item, out bool _) ? new(Color.AntiqueWhite, 0.1f) : new(Color.AntiqueWhite, 0.4f),
                            thickness: 2.0f
                        );
                    }
                    else if (DebugDrawStructure && entity is Structure structure)
                    {
                        RectangleF entityAABB = AABB.CalculateFixed(structure);
                        entityAABB.Offset(entity.DrawPosition);
                        GUI.DrawRectangle(
                            spriteBatch,
                            new Vector2[]
                            {
                                cam.WorldToScreen(new(entityAABB.X, entityAABB.Y)),
                                cam.WorldToScreen(new(entityAABB.X + entityAABB.Width, entityAABB.Y)),
                                cam.WorldToScreen(new(entityAABB.X + entityAABB.Width, entityAABB.Y - entityAABB.Height)),
                                cam.WorldToScreen(new(entityAABB.X, entityAABB.Y - entityAABB.Height)),
                            },
                            isEntityCulled.TryGetValue(structure, out bool _) ? new(Color.Green, 0.1f) : new(Color.Green, 0.4f),
                            thickness: 2.0f
                        );
                    }
                }

                if (DebugDrawCharacter)
                {
                    foreach (var character in Character.CharacterList)
                    {
                        if (character.IsVisible && character != LightManager.ViewTarget)
                        {
                            RectangleF entityAABB = AABB.CalculateDynamic(character);
                            GUI.DrawRectangle(
                                spriteBatch,
                                new Vector2[]
                                {
                                    cam.WorldToScreen(new(entityAABB.X, entityAABB.Y)),
                                    cam.WorldToScreen(new(entityAABB.X + entityAABB.Width, entityAABB.Y)),
                                    cam.WorldToScreen(new(entityAABB.X + entityAABB.Width, entityAABB.Y - entityAABB.Height)),
                                    cam.WorldToScreen(new(entityAABB.X, entityAABB.Y - entityAABB.Height)),
                                },
                                isEntityCulled.TryGetValue(character, out bool _) ? new(Color.Red, 0.2f) : Color.Red,
                                thickness: 3.0f
                            );
                        }
                    }
                }
                spriteBatch.End();
            }
        }
    }
}