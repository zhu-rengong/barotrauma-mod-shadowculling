using Barotrauma.Items.Components;
using Barotrauma.Lights;
using HarmonyLib;
using Microsoft.Xna.Framework.Graphics;
using Emit = System.Reflection.Emit;

namespace ShadowCulling;

/// <summary>Contains Harmony patches for modifying game behavior to support shadow culling.</summary>
[HarmonyPatch]
public static partial class Patches
{
#if CLIENT
    /// <summary>
    /// Returns zero size since light rendering is independent of culling and we don't need its DrawSize for AABB calculation.
    /// </summary>
    [HarmonyPatch(
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

    private static void MainViewportDrawStart()
    {
        Plugin.IsDrawingInMainViewport = true;
        Plugin.TicksUntilNextCull++;
        if (Plugin.LastCullingUpdateTime <= Timing.TotalTime - Plugin.CullingInterval)
        {
            Plugin.IsCullPerformable = true;
        }
    }

    private static void MainViewportDrawEnd()
    {
        Plugin.IsDrawingInMainViewport = false;
        if (Plugin.IsCullPerformable)
        {
            Plugin.TicksUntilNextCull = 0;
            Plugin.LastCullingUpdateTime = Timing.TotalTime;
            Plugin.IsCullPerformable = false;

            GameMain.PerformanceCounter.AddElapsedTicks("Draw:ShadowCulling", Plugin.CullTickAccumulator);
            Plugin.CullTickAccumulator = 0;
        }
    }

    [HarmonyPatch(typeof(GameScreen), nameof(GameScreen.Draw)), HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> GameScreen_Draw_Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        try
        {
            var codeMatcher = new CodeMatcher(instructions);
            codeMatcher.MatchStartForward(
                new CodeMatch(IsLoadArgument),
                new CodeMatch(IsLoadArgument),
                new CodeMatch(IsLoadArgument),
                new CodeMatch(IsLoadArgument),
                new CodeMatch(OpCodes.Call, AccessTools.Method(typeof(GameScreen), nameof(GameScreen.DrawMap))));
            codeMatcher.ThrowIfInvalid($"Not found instructions for GameScreen.DrawMap call!");
            codeMatcher.Insert(new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Patches), nameof(Patches.MainViewportDrawStart))));
            codeMatcher.MatchEndForward(new CodeMatch(OpCodes.Call, AccessTools.Method(typeof(GameScreen), nameof(GameScreen.DrawMap))));
            codeMatcher.InsertAfter(new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Patches), nameof(Patches.MainViewportDrawEnd))));

            return codeMatcher.InstructionEnumeration();
        }
        catch (Exception ex)
        {
            Plugin.DebugConsole.ThrowError($"Transpiler error", ex);
            return instructions;
        }
    }

    [HarmonyPatch(typeof(LightManager), nameof(LightManager.UpdateObstructVision)), HarmonyFinalizer]
    private static void LightManager_UpdateObstructVision_Finalizer()
    {
        if (Plugin.IsCullPerformable && Plugin.IsCullingAllowed())
        {
            Plugin.PerformEntityCulling();
        }
    }

    [HarmonyPatch(typeof(Submarine), nameof(Submarine.DrawBack)), HarmonyPrefix]
    private static void Submarine_DrawBack_Prefix(ref Predicate<MapEntity>? predicate)
        => InjectRenderCulling(ref predicate);

    [HarmonyPatch(typeof(Submarine), nameof(Submarine.DrawDamageable)), HarmonyPrefix]
    private static void Submarine_DrawDamageable_Prefix(ref Predicate<MapEntity>? predicate)
        => InjectRenderCulling(ref predicate);

    [HarmonyPatch(typeof(Submarine), nameof(Submarine.DrawFront)), HarmonyPrefix]
    private static void Submarine_DrawFront_Prefix(ref Predicate<MapEntity>? predicate)
        => InjectRenderCulling(ref predicate);

    private static void InjectRenderCulling(ref Predicate<MapEntity>? predicate)
    {
        if (Plugin.IsCullingAllowed())
        {
            var originalPredicate = predicate;

            predicate = originalPredicate == null
                ? entity => !Plugin.IsEntityCulled.GetValue(entity)
                : entity => !Plugin.IsEntityCulled.GetValue(entity) && originalPredicate(entity);
        }
    }

    [HarmonyPatch(typeof(Character), nameof(Character.Draw)), HarmonyPrefix]
    private static bool Character_Draw_Prefix(Character __instance)
    {
        if (!Plugin.IsCullingAllowed())
        {
            return true;
        }
        return !Plugin.IsEntityCulled.GetValue(__instance);
    }

    [HarmonyPatch(typeof(Entity), nameof(Entity.RemoveAll)), HarmonyFinalizer]
    private static void Entity_RemoveAll_Finalizer()
    {
        Plugin.TryClearAll();
    }

    [HarmonyPatch(typeof(LightManager), nameof(LightManager.UpdateObstructVision)), HarmonyTranspiler]
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
            Plugin.DebugConsole.ThrowError($"Transpiler error", ex);
            return instructions;
        }
    }

    [HarmonyPatch(typeof(Structure), nameof(Structure.IsVisible)), HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> Structure_IsVisible_Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        try
        {
            var codeMatcher = new CodeMatcher(instructions);

            codeMatcher.End();
            codeMatcher.MatchStartBackwards(
                new CodeMatch(IsLoadLocal),
                new CodeMatch(IsLoadLocal),
                new CodeMatch(
                    OpCodes.Call,
                    AccessTools.Method(typeof(Vector2), "op_Subtraction", [typeof(Vector2), typeof(Vector2)])));

            codeMatcher.ThrowIfInvalid($"Not found instructions for max - min!");

            // The instruction the matcher stopped on is the target of the branch that skips the `return false` above,
            // so the injected call has to take that label over - without it the branch steps over the injection. The
            // copy constructor keeps labels, hence the clear: only the first injected instruction may carry it.
            var loadThis = new CodeInstruction(OpCodes.Ldarg_0);
            var loadMax = new CodeInstruction(codeMatcher.Instruction);
            loadMax.labels.Clear();
            var loadMin = new CodeInstruction(codeMatcher.InstructionAt(1));
            codeMatcher.Instruction.MoveLabelsTo(loadThis);
            codeMatcher.Insert(
                loadThis,
                loadMax,
                loadMin,
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Plugin), nameof(Plugin.CacheStructureVisibleExtents))));

            return codeMatcher.InstructionEnumeration();
        }
        catch (Exception ex)
        {
            Plugin.DebugConsole.ThrowError($"Transpiler error", ex);
            return instructions;
        }
    }

    public static bool IsLoadLocal(CodeInstruction instruction)
    {
        return instruction.opcode == OpCodes.Ldloc_S
            || instruction.opcode == OpCodes.Ldloc
            || (instruction.opcode.Value >= OpCodes.Ldloc_0.Value && instruction.opcode.Value <= OpCodes.Ldloc_3.Value);
    }

    public static bool IsLoadArgument(CodeInstruction instruction)
    {
        return instruction.opcode == OpCodes.Ldarg_S
            || instruction.opcode == OpCodes.Ldarg
            || (instruction.opcode.Value >= OpCodes.Ldarg_0.Value && instruction.opcode.Value <= OpCodes.Ldarg_3.Value);
    }
#endif
}
