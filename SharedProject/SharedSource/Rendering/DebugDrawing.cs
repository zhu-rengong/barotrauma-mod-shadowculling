using Barotrauma.Items.Components;
using Barotrauma.Lights;
using HarmonyLib;
using Microsoft.Xna.Framework.Graphics;

namespace ShadowCulling;

/// <summary>
/// Debug visualization for shadow culling: draws culling hulls, shadow casters,
/// entity bounds and character bounds over the game world.
/// </summary>
public static partial class Patches
{
#if CLIENT
    /// <summary>Patch for GameScreen.DrawMap to render debug visualization.</summary>
    [HarmonyPatch(
        declaringType: typeof(GameScreen),
        methodName: nameof(GameScreen.DrawMap)
    )]
    private static class GameScreen_DrawMap
    {
        static void Postfix(SpriteBatch spriteBatch)
        {
            if (!Plugin.DebugDrawingEnabled || GameMain.GameScreen.Cam is not Camera camera)
            {
                return;
            }

            spriteBatch.Begin(SpriteSortMode.Deferred, null, GUI.SamplerState, null, GameMain.ScissorTestEnable);

            if (Plugin.DebugDrawingHull)
            {
                DrawDebugHulls(spriteBatch, camera);
            }

            if (Plugin.DebugDrawingShadow)
            {
                DrawDebugShadows(spriteBatch, camera);
            }

            DrawDebugEntities(spriteBatch, camera);

            if (Plugin.DebugDrawingCharacter)
            {
                DrawDebugCharacters(spriteBatch, camera);
            }

            spriteBatch.End();
        }

        private static void DrawDebugHulls(SpriteBatch spriteBatch, Camera camera)
        {
            foreach (Hull hull in Plugin.HullsForCulling)
            {
                RectangleF worldRect = hull.WorldRect;
                Color hullColor = Plugin.IsEntityCulled.GetValue(hull)
                    ? new Color(Color.MediumPurple, 0.2f)
                    : Color.MediumPurple;

                GUI.DrawRectangle(
                    spriteBatch,
                    [
                        camera.WorldToScreen(new(worldRect.X, worldRect.Y)),
                        camera.WorldToScreen(new(worldRect.X + worldRect.Width, worldRect.Y)),
                        camera.WorldToScreen(new(worldRect.X + worldRect.Width, worldRect.Y - worldRect.Height)),
                        camera.WorldToScreen(new(worldRect.X, worldRect.Y - worldRect.Height)),
                    ],
                    hullColor,
                    thickness: 4.0f
                );
            }
        }

        private static void DrawDebugShadows(SpriteBatch spriteBatch, Camera camera)
        {
            foreach (int shadowIndex in Plugin.SortedShadowIndices)
            {
                ref readonly Shadow shadow = ref Plugin.ValidShadowBuffer[shadowIndex];

                GUI.DrawLine(
                    spriteBatch,
                    camera.WorldToScreen(shadow.Occluder.Start),
                    camera.WorldToScreen(shadow.Occluder.End),
                    Color.BlueViolet,
                    width: 3
                );

                if (Plugin.DebugDrawingShadowLength > 0.0f)
                {
                    DrawShadowRays(spriteBatch, camera, shadow);
                }
            }
        }

        private static void DrawShadowRays(SpriteBatch spriteBatch, Camera camera, in Shadow shadow)
        {
            Vector2 ray1End = camera.WorldToScreen(shadow.Ray1.Origin + shadow.Ray1.Direction * Plugin.DebugDrawingShadowLength);
            Vector2 ray2End = camera.WorldToScreen(shadow.Ray2.Origin + shadow.Ray2.Direction * Plugin.DebugDrawingShadowLength);

            GUI.DrawLine(
                spriteBatch,
                camera.WorldToScreen(shadow.Ray1.Origin),
                ray1End,
                Color.BlueViolet,
                width: 1
            );

            GUI.DrawLine(
                spriteBatch,
                camera.WorldToScreen(shadow.Ray2.Origin),
                ray2End,
                Color.BlueViolet,
                width: 1
            );
        }

        private static void DrawDebugEntities(SpriteBatch spriteBatch, Camera camera)
        {
            foreach (MapEntity entity in Submarine.VisibleEntities)
            {
                if (entity.IsHidden) { continue; }

                if (Plugin.DebugDrawingItem && entity is Item item)
                {
                    DrawDebugItem(spriteBatch, camera, item);
                }
                else if (Plugin.DebugDrawingStructure && entity is Structure structure)
                {
                    DrawDebugStructure(spriteBatch, camera, structure);
                }
            }
        }

        private static void DrawDebugItem(SpriteBatch spriteBatch, Camera camera, Item item)
        {
            if (!item.cachedVisibleExtents.HasValue || !item.Visible || item.isWire)
            {
                return;
            }

            RectangleF entityAABB = item.cachedVisibleExtents.Value;
            entityAABB.Width -= entityAABB.X;
            entityAABB.Height -= entityAABB.Y;
            entityAABB.Y += entityAABB.Height;
            entityAABB.Offset(item.DrawPosition);

            Color itemColor = Plugin.IsEntityCulled.GetValue(item)
                ? new Color(Color.AntiqueWhite, 0.1f)
                : new Color(Color.AntiqueWhite, 0.4f);

            GUI.DrawRectangle(
                spriteBatch,
                [
                    camera.WorldToScreen(new(entityAABB.X, entityAABB.Y)),
                    camera.WorldToScreen(new(entityAABB.X + entityAABB.Width, entityAABB.Y)),
                    camera.WorldToScreen(new(entityAABB.X + entityAABB.Width, entityAABB.Y - entityAABB.Height)),
                    camera.WorldToScreen(new(entityAABB.X, entityAABB.Y - entityAABB.Height)),
                ],
                itemColor,
                thickness: 2.0f
            );
        }

        private static void DrawDebugStructure(SpriteBatch spriteBatch, Camera camera, Structure structure)
        {
            RectangleF entityAABB = Plugin.EntityVisibleExtents.GetValue(structure);
            entityAABB.Offset(structure.DrawPosition);

            Color structureColor = Plugin.IsEntityCulled.GetValue(structure)
                ? new Color(Color.Green, 0.1f)
                : new Color(Color.Green, 0.4f);

            GUI.DrawRectangle(
                spriteBatch,
                [
                    camera.WorldToScreen(new(entityAABB.X, entityAABB.Y)),
                    camera.WorldToScreen(new(entityAABB.X + entityAABB.Width, entityAABB.Y)),
                    camera.WorldToScreen(new(entityAABB.X + entityAABB.Width, entityAABB.Y - entityAABB.Height)),
                    camera.WorldToScreen(new(entityAABB.X, entityAABB.Y - entityAABB.Height)),
                ],
                structureColor,
                thickness: 2.0f
            );
        }

        private static void DrawDebugCharacters(SpriteBatch spriteBatch, Camera camera)
        {
            foreach (Character character in Character.CharacterList)
            {
                if (!character.IsVisible || character == LightManager.ViewTarget)
                {
                    continue;
                }

                RectangleF entityAABB = EntityBounds.CalculateDynamic(character);
                Color characterColor = Plugin.IsEntityCulled.GetValue(character)
                    ? new Color(Color.Red, 0.2f)
                    : Color.Red;

                GUI.DrawRectangle(
                    spriteBatch,
                    [
                        camera.WorldToScreen(new(entityAABB.X, entityAABB.Y)),
                        camera.WorldToScreen(new(entityAABB.X + entityAABB.Width, entityAABB.Y)),
                        camera.WorldToScreen(new(entityAABB.X + entityAABB.Width, entityAABB.Y - entityAABB.Height)),
                        camera.WorldToScreen(new(entityAABB.X, entityAABB.Y - entityAABB.Height)),
                    ],
                    characterColor,
                    thickness: 3.0f
                );
            }
        }
    }
#endif
}
