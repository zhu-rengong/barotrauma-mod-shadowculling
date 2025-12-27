using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Barotrauma;
using Barotrauma.Items.Components;
using FarseerPhysics;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

#if CLIENT
[assembly: IgnoresAccessChecksTo("Barotrauma")]
#endif
#if SERVER
[assembly: IgnoresAccessChecksTo("DedicatedServer")]
#endif
[assembly: IgnoresAccessChecksTo("BarotraumaCore")]

namespace Whosyouradddy.ShadowCulling
{
    public partial class Plugin : IAssemblyPlugin
    {
        private Harmony? harmony;

        public void Initialize()
        {
            harmony = new Harmony("com.whosyouradddy.shadowculling");
            harmony.PatchAll();

            AddCommands();

            InitializeProjSpecific();
        }


        public void OnLoadCompleted()
        {

        }

        public void PreInitPatching()
        {

        }

        public partial void InitializeProjSpecific();

        public void Dispose()
        {
            harmony?.UnpatchSelf();
            harmony = null;

            RemoveCommands();
        }

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
                    PerformEntityCulling();
                }
            }
        }

        [HarmonyPatch(
            declaringType: typeof(Entity),
            methodName: nameof(Entity.RemoveAll)
        )]
        class Entity_RemoveAll
        {
            static void Postfix()
            {
                hullsForCulling.Clear();
                entitiesForCulling.Clear();
                charactersForCulling.Clear();
                isEntityCulled.Clear();
                entityHull.Clear();
                LuaCsLogger.LogMessage($"Mod:ShadowCulling | Reset");
            }
        }

        [HarmonyPatch(
            declaringType: typeof(Submarine),
            methodName: nameof(Submarine.DrawBack)
        )]
        class Submarine_DrawBack
        {
            static bool Prefix(SpriteBatch spriteBatch, bool editing = false, Predicate<MapEntity>? predicate = null)
            {
                if (DisallowCulling) { return true; }

                var entitiesToRender = !editing && Submarine.visibleEntities != null ? Submarine.visibleEntities : MapEntity.MapEntityList;

                foreach (MapEntity e in entitiesToRender)
                {
                    if (!e.DrawBelowWater || isEntityCulled.TryGetValue(e, out bool _)) continue;

                    if (predicate != null)
                    {
                        if (!predicate(e)) continue;
                    }

                    e.Draw(spriteBatch, editing, true);
                }

                return false;
            }
        }

        [HarmonyPatch(
            declaringType: typeof(Submarine),
            methodName: nameof(Submarine.DrawFront)
        )]
        class Submarine_DrawFront
        {
            static bool Prefix(SpriteBatch spriteBatch, bool editing = false, Predicate<MapEntity>? predicate = null)
            {
                if (DisallowCulling) { return true; }

                var entitiesToRender = !editing && Submarine.visibleEntities != null ? Submarine.visibleEntities : MapEntity.MapEntityList;

                foreach (MapEntity e in entitiesToRender)
                {
                    if (!e.DrawOverWater || isEntityCulled.TryGetValue(e, out bool _)) { continue; }

                    if (predicate != null)
                    {
                        if (!predicate(e)) { continue; }
                    }

                    e.Draw(spriteBatch, editing, false);
                }

                if (GameMain.DebugDraw)
                {
                    foreach (Submarine sub in Submarine.Loaded)
                    {
                        Rectangle worldBorders = sub.Borders;
                        worldBorders.Location += (sub.DrawPosition + sub.HiddenSubPosition).ToPoint();
                        worldBorders.Y = -worldBorders.Y;

                        GUI.DrawRectangle(spriteBatch, worldBorders, Color.White, false, 0, 5);

                        if (sub.SubBody == null || sub.subBody.PositionBuffer.Count < 2) continue;

                        Vector2 prevPos = ConvertUnits.ToDisplayUnits(sub.subBody.PositionBuffer[0].Position);
                        prevPos.Y = -prevPos.Y;

                        for (int i = 1; i < sub.subBody.PositionBuffer.Count; i++)
                        {
                            Vector2 currPos = ConvertUnits.ToDisplayUnits(sub.subBody.PositionBuffer[i].Position);
                            currPos.Y = -currPos.Y;

                            GUI.DrawRectangle(spriteBatch, new Rectangle((int)currPos.X - 10, (int)currPos.Y - 10, 20, 20), Color.Blue * 0.6f, true, 0.01f);
                            GUI.DrawLine(spriteBatch, prevPos, currPos, Color.Cyan * 0.5f, 0, 5);

                            prevPos = currPos;
                        }
                    }
                }

                return false;
            }
        }

        [HarmonyPatch(
            declaringType: typeof(Submarine),
            methodName: nameof(Submarine.DrawDamageable)
        )]
        class Submarine_DrawDamageable
        {
            static bool Prefix(SpriteBatch spriteBatch, Effect damageEffect, bool editing = false, Predicate<MapEntity>? predicate = null)
            {
                if (DisallowCulling) { return true; }

                var entitiesToRender = !editing && Submarine.visibleEntities != null ? Submarine.visibleEntities : MapEntity.MapEntityList;

                Submarine.depthSortedDamageable.Clear();

                //insertion sort according to draw depth
                foreach (MapEntity e in entitiesToRender)
                {
                    if (e is Structure structure && structure.DrawDamageEffect && !isEntityCulled.TryGetValue(structure, out bool _))
                    {
                        if (predicate != null)
                        {
                            if (!predicate(e)) { continue; }
                        }
                        float drawDepth = structure.GetDrawDepth();
                        int i = 0;
                        while (i < Submarine.depthSortedDamageable.Count)
                        {
                            float otherDrawDepth = Submarine.depthSortedDamageable[i].GetDrawDepth();
                            if (otherDrawDepth < drawDepth) { break; }
                            i++;
                        }
                        Submarine.depthSortedDamageable.Insert(i, structure);
                    }
                }

                foreach (Structure s in Submarine.depthSortedDamageable)
                {
                    s.DrawDamage(spriteBatch, damageEffect, editing);
                }

                return false;
            }
        }

        [HarmonyPatch(
            declaringType: typeof(Character),
            methodName: nameof(Character.Draw)
        )]
        class Character_Draw
        {
            static bool Prefix(Character __instance)
            {
                if (isEntityCulled.TryGetValue(__instance, out bool _))
                {
                    return false;
                }

                return true;
            }
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

        // In Vanilla, if you are too far from the center of a ladder,
        // you won't be able to see it. We can fix this by modifying its draw size.
        [HarmonyPatch(
            declaringType: typeof(Ladder),
            methodName: nameof(Ladder.DrawSize),
            methodType: MethodType.Getter
        )]
        class Ladder_DrawSize
        {
            static bool Prefix(Ladder __instance, ref Vector2 __result)
            {
                if (__instance.backgroundSprite == null) { return true; }
                __result.X = __instance.backgroundSprite.size.X * __instance.item.Scale;
                __result.Y = __instance.item.Rect.Height;
                return false;
            }
        }
    }
}
