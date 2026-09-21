using HarmonyLib;

namespace ShadowCulling;

public partial class Plugin : IAssemblyPlugin
{
    // Assigned by the plugin service after the constructor has run.
    #pragma warning disable CS8618
    public IConfigService ConfigService { get; set; }
    public IPluginManagementService PluginManagementService { get; set; }
    public ILoggerService loggerService { get; set; }
    public IConsoleCommandsService ConsoleCommandsService { get; set; }
    
    public static ILoggerService LoggerService;
    #pragma warning restore CS8618

    private ContentPackage _package = null!;

    private Harmony? harmony;

    [MethodImpl(MethodImplOptions.NoOptimization)]
    public void Initialize()
    {
        LoggerService = loggerService;

        if (!PluginManagementService.TryGetPackageForPlugin<Plugin>(out _package))
        {
            loggerService.LogError("Failed to find package!");
            return;
        }

        harmony = new("shadowculling");
        harmony.PatchAll();

        LoadConfig();
        RegisterCommands();

        InitializeProjectSpecific();
    }

    public partial void InitializeProjectSpecific();

    [MethodImpl(MethodImplOptions.NoOptimization)]
    public void OnLoadCompleted()
    {
    }

    [MethodImpl(MethodImplOptions.NoOptimization)]
    public void PreInitPatching()
    {
    }

    [MethodImpl(MethodImplOptions.NoOptimization)]
    public void Dispose()
    {
        harmony?.UnpatchSelf();
        harmony = null;
    }

}