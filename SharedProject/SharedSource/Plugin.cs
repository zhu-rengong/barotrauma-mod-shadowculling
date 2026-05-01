using HarmonyLib;

namespace ShadowCulling;

public partial class Plugin : IBarotraumaPlugin
{
    public const string ModPrefix = "shadowculling";
    public static readonly IDebugConsole DebugConsole = PluginServiceProvider.GetService<IDebugConsole>();
    public static readonly ISettingsService SettingsService = PluginServiceProvider.GetService<ISettingsService>();
    public static readonly IHarmonyProvider HarmonyProvider = PluginServiceProvider.GetService<IHarmonyProvider>();

    public ContentPackage _package = null!;

    public Harmony? harmony;

    [MethodImpl(MethodImplOptions.NoOptimization)]
    public void Init()
    {
        HarmonyProvider.PatchAll();

        CreateSettings();
        RegisterCommands();

        InitializeProjectSpecific();
    }

    public partial void InitializeProjectSpecific();

    [MethodImpl(MethodImplOptions.NoOptimization)]
    public void OnContentLoaded()
    {
 
    }

    [MethodImpl(MethodImplOptions.NoOptimization)]
    public void Dispose()
    {

    }
}