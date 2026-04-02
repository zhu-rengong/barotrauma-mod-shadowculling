using HarmonyLib;
using Barotrauma;
using Microsoft.Xna.Framework;
using Barotrauma.Plugins;
using Barotrauma.Items.Components;
using FarseerPhysics;
using Microsoft.Xna.Framework.Graphics;
using Barotrauma.Lights;
using ShadowCulling.Geometry;

namespace ShadowCulling;

/// <summary>
/// Contains Harmony patches for modifying game behavior to support shadow culling.
/// </summary>
[HarmonyLib.HarmonyPatch]
public static class HarmonyPatch
{
#if CLIENT
    /// <summary>
    /// Returns zero size since light rendering is independent of culling and we don't need its DrawSize for AABB calculation.
    /// </summary>
    [HarmonyLib.HarmonyPatch(
        declaringType: typeof(LightComponent),
        methodName: nameof(LightComponent.DrawSize),
        methodType: MethodType.Getter
    )]
    private static class LightComponent_DrawSize
    {
        static bool Prefix(ref Vector2 __result)
        {
            __result.X = 0.0f;
            __result.Y = 0.0f;
            return false;
        }
    }

    ///// <summary>
    ///// In Vanilla, if you are too far from the center of a ladder, you won't be able to see it.
    ///// </summary>
    //[HarmonyLib.HarmonyPatch(
    //    declaringType: typeof(Ladder),
    //    methodName: nameof(Ladder.DrawSize),
    //    methodType: MethodType.Getter
    //)]
    //private static class Ladder_DrawSize
    //{
    //    static bool Prefix(Ladder __instance, ref Vector2 __result)
    //    {
    //        // if (__instance.backgroundSprite == null) { return true; }
    //        // __result.X = __instance.backgroundSprite.size.X * __instance.item.Scale;
    //        // __result.Y = __instance.item.Rect.Height;
    //        // return false;
    //    }
    //}

    #region Culling Integration

    [HarmonyLib.HarmonyPatch(
        declaringType: typeof(Submarine),
        methodName: nameof(Submarine.CullEntities)
    )]
    private static class Submarine_CullEntities
    {
        static void Postfix()
        {
            Plugin.PerformEntityCulling();
        }
    }

    [HarmonyLib.HarmonyPatch(
        declaringType: typeof(Entity),
        methodName: nameof(Entity.RemoveAll)
    )]
    private static class Entity_RemoveAll
    {
        static void Postfix()
        {
            Plugin.TryClearAll();
        }
    }

    #endregion

    #region Rendering Patches

    [HarmonyLib.HarmonyPatch(
        declaringType: typeof(Submarine),
        methodName: nameof(Submarine.DrawBack)
    )]
    private static class Submarine_DrawBack
    {
        static void Prefix(ref Predicate<MapEntity>? predicate)
        {
            if (Plugin.DisallowCulling) { return; }

            var originalPredicate = predicate;

            predicate = entity =>
                !Plugin.IsEntityCulled.TryGetValue(entity, out bool _)
                && (originalPredicate == null || originalPredicate(entity));
        }
    }

    [HarmonyLib.HarmonyPatch(
        declaringType: typeof(Submarine),
        methodName: nameof(Submarine.DrawFront)
    )]
    private static class Submarine_DrawFront
    {
        static void Prefix(ref Predicate<MapEntity>? predicate)
        {
            if (Plugin.DisallowCulling) { return; }

            var originalPredicate = predicate;

            predicate = entity =>
                !Plugin.IsEntityCulled.TryGetValue(entity, out bool _)
                && (originalPredicate == null || originalPredicate(entity));
        }
    }

    [HarmonyLib.HarmonyPatch(
        declaringType: typeof(Submarine),
        methodName: nameof(Submarine.DrawDamageable)
    )]
    private static class Submarine_DrawDamageable
    {
        static void Prefix(ref Predicate<MapEntity>? predicate)
        {
            if (Plugin.DisallowCulling) { return; }

            var originalPredicate = predicate;

            predicate = entity =>
                !Plugin.IsEntityCulled.TryGetValue(entity, out bool _)
                && (originalPredicate == null || originalPredicate(entity));
        }
    }

    /// <summary>
    /// Patch for Character.Draw to cull character rendering.
    /// </summary>
    [HarmonyLib.HarmonyPatch(
        declaringType: typeof(Character),
        methodName: nameof(Character.Draw)
    )]
    private static class Character_Draw
    {
        static bool Prefix(Character __instance)
        {
            return !Plugin.IsEntityCulled.TryGetValue(__instance, out bool _);
        }
    }

    #endregion

    #region Debug Drawing

    /// <summary>
    /// Patch for GameScreen.DrawMap to render debug visualization.
    /// </summary>
    [HarmonyLib.HarmonyPatch(
        declaringType: typeof(GameScreen),
        methodName: nameof(GameScreen.DrawMap)
    )]
    private static class GameScreen_DrawMap
    {
        static void Postfix(SpriteBatch spriteBatch)
        {
            if (!Plugin.DebugDrawingEnabled || Plugin.DisallowCulling || GameMain.GameScreen.Cam is not Camera camera)
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
                Color hullColor = Plugin.IsEntityCulled.TryGetValue(hull, out bool _)
                    ? new Color(Color.MediumPurple, 0.5f)
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

            Color itemColor = Plugin.IsEntityCulled.TryGetValue(item, out bool _)
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
            RectangleF entityAABB = AABB.CalculateFixed(structure);
            entityAABB.Offset(structure.DrawPosition);

            Color structureColor = Plugin.IsEntityCulled.TryGetValue(structure, out bool _)
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

                RectangleF entityAABB = AABB.CalculateDynamic(character);
                Color characterColor = Plugin.IsEntityCulled.TryGetValue(character, out bool _)
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

    #endregion

#endif
}
