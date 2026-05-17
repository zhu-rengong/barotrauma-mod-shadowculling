using Barotrauma.Items.Components;
using Barotrauma.Lights;
using HarmonyLib;
using Microsoft.Xna.Framework.Graphics;
using ShadowCulling.Geometry;
using Emit = System.Reflection.Emit;

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


    [HarmonyLib.HarmonyPatch(typeof(Submarine), nameof(Submarine.DrawBack)), HarmonyPrefix]
    static void Submarine_DrawBack_Prefix(ref Predicate<MapEntity>? predicate)
        => InjectRenderCulling(ref predicate);

    [HarmonyLib.HarmonyPatch(typeof(Submarine), nameof(Submarine.DrawDamageable)), HarmonyPrefix]
    static void Submarine_DrawDamageable_Prefix(ref Predicate<MapEntity>? predicate)
        => InjectRenderCulling(ref predicate);

    [HarmonyLib.HarmonyPatch(typeof(Submarine), nameof(Submarine.DrawFront)), HarmonyPrefix]
    static void Submarine_DrawFront_Prefix(ref Predicate<MapEntity>? predicate)
        => InjectRenderCulling(ref predicate);

    static void InjectRenderCulling(ref Predicate<MapEntity>? predicate)
    {
        if (!Plugin.DisallowCulling)
        {
            var originalPredicate = predicate;

            predicate = originalPredicate == null
                 ? entity => !Plugin.IsEntityCulled.GetValue(entity)
                 : entity => !Plugin.IsEntityCulled.GetValue(entity) && originalPredicate(entity);
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
            return !Plugin.IsEntityCulled.GetValue(__instance);
        }
    }

    #endregion

    #region LOS Position Capture

    [HarmonyLib.HarmonyPatch(typeof(LightManager), nameof(LightManager.UpdateObstructVision)), HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> LightManager_UpdateObstructVision_Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        try
        {
            var codeMatcher = new CodeMatcher(instructions);

            /*
            call class Barotrauma.Entity Barotrauma.Lights.LightManager::get_ViewTarget()
            callvirt instance valuetype[XNATypes] Microsoft.Xna.Framework.Vector2 Barotrauma.Entity::get_DrawPosition()
            stloc.s pos (9)
            */
            codeMatcher.MatchEndForward(
                new(OpCodes.Callvirt, AccessTools.PropertyGetter(typeof(Entity), nameof(Entity.DrawPosition))),
                new(OpCodes.Stloc_S)
            );
            int posLocalIndex = (codeMatcher.Instruction.operand as Emit.LocalBuilder)!.LocalIndex;

            /*
            ldc.i4.0
            stloc.s centeredOnHead(10)
            */
            codeMatcher.MatchEndForward(
                new(OpCodes.Ldc_I4_0),
                new(OpCodes.Stloc_S)
            );
            int centeredOnHeadLocalIndex = (codeMatcher.Instruction.operand as Emit.LocalBuilder)!.LocalIndex;

            /*
            ldloc.s centeredOnHead(10)
            brfalse 301(03C9) ldloc.s convexHulls(15)
            */
            codeMatcher.MatchEndForward([new(OpCodes.Call, AccessTools.Method(typeof(ConvexHull), nameof(ConvexHull.GetHullsInRange)))]);
            codeMatcher.MatchEndForward(
                new(OpCodes.Ldloc_S),
                new(OpCodes.Brfalse)
            );
            var jumpTarget = (Emit.Label)codeMatcher.Instruction.operand;

            /*
            ldloc.s	convexHulls (15)
            brfalse	429 (056C) ldarg.1
             */
            codeMatcher.SearchForward(ci => ci.labels.Contains(jumpTarget));

            /*
            Plugin.ViewPos = pos;
            */
            var ldlocPos = new CodeInstruction(OpCodes.Ldloc_S, (byte)posLocalIndex);
            ldlocPos.MoveLabelsFrom(codeMatcher.Instruction);
            codeMatcher.Insert(
                ldlocPos,
                new(OpCodes.Stsfld, AccessTools.Field(typeof(Plugin), nameof(Plugin.ViewPosHijacked)))
            );

            return codeMatcher.InstructionEnumeration();
        }
        catch (Exception ex)
        {
            Plugin.DebugConsole.ThrowError("Transpiler error", ex);
            return instructions;
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
            RectangleF entityAABB = AABB.CalculateFixed(structure);
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

                RectangleF entityAABB = AABB.CalculateDynamic(character);
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

    #endregion

#endif
}
