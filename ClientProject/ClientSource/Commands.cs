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

        public static bool DebugDrawAABB = false;

        public static bool DebugLog = false;

        public static void AddCommands()
        {
            AddedCommands.Add(new DebugConsole.Command("shadowculling_debugonce", "", (string[] args) =>
            {
                CullEntities();
            }));

            AddedCommands.Add(new DebugConsole.Command("shadowculling_toggle", "", (string[] args) =>
            {
                CullingEnabled = !CullingEnabled;
                if (!CullingEnabled)
                {
                    Item.ItemList.ForEach(item =>
                    {
                        item.Visible = true;
                    });
                }
            }));

            AddedCommands.Add(new DebugConsole.Command("shadowculling_debugdrawaabb", "", (string[] args) =>
            {
                DebugDrawAABB = !DebugDrawAABB;
            }));

            AddedCommands.Add(new DebugConsole.Command("shadowculling_debuglog", "", (string[] args) =>
            {
                DebugLog = !DebugLog;
            }));

            DebugConsole.Commands.AddRange(AddedCommands);
        }

        public static void RemoveCommands()
        {
            AddedCommands.ForEach(c => DebugConsole.Commands.Remove(c));
            AddedCommands.Clear();
        }
    }
}
