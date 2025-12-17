using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Barotrauma;
using Barotrauma.Extensions;
using Barotrauma.Lights;
using Barotrauma.Networking;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace Whosyouradddy.ShadowCulling
{
    public partial class Plugin : IAssemblyPlugin
    {
        public static List<DebugConsole.Command> AddedCommands = new List<DebugConsole.Command>();

        public static bool CullingEnabled = true;

        public static bool DebugDraw = false;

        public static bool DebugLog = false;

        public static void AddCommands()
        {
            AddedCommands.Add(new DebugConsole.Command("shadowcullingdebugonce", "", (string[] args) =>
            {
                PerformEntityCulling();
            }, isCheat: false));

            AddedCommands.Add(new DebugConsole.Command("shadowcullingtoggle", "", (string[] args) =>
            {
                CullingEnabled = !CullingEnabled;
                if (!CullingEnabled)
                {
                    Item.ItemList.ForEach(item =>
                    {
                        item.Visible = true;
                    });
                }
            }, isCheat: false));

            AddedCommands.Add(new DebugConsole.Command("shadowcullingdebugdraw", "", (string[] args) =>
            {
                DebugDraw = !DebugDraw;
            }, isCheat: false));

            AddedCommands.Add(new DebugConsole.Command("shadowcullingdebuglog", "", (string[] args) =>
            {
                DebugLog = !DebugLog;
            }, isCheat: false));

            AddedCommands.Add(new DebugConsole.Command("shadowcullingpoolstat", "", (string[] args) =>
            {
                LuaCsLogger.LogMessage($"[PooledLinkedList] Segment: {poolLinkedListSegment.Count}");
            }, isCheat: false));

            DebugConsole.Commands.AddRange(AddedCommands);
        }

        public static void RemoveCommands()
        {
            AddedCommands.ForEach(c => DebugConsole.Commands.Remove(c));
            AddedCommands.Clear();
        }

        [HarmonyPatch(
            declaringType: typeof(LuaGame),
            methodName: nameof(LuaGame.IsCustomCommandPermitted)
        )]
        class LuaGame_IsCustomCommandPermitted
        {
            static void Postfix(Identifier command, ref bool __result)
            {
                if (AddedCommands.Any(c => c.Names.Contains(command.Value))) { __result = true; }
            }
        }
    }
}
