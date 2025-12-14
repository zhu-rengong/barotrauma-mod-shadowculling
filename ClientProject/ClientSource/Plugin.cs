using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Barotrauma;
using HarmonyLib;

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
    }
}
