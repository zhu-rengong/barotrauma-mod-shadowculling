using HarmonyLib;

namespace ShadowCulling;

public partial class Plugin : IAssemblyPlugin
{
    // These are automatically assigned by the plugin service after the Constructor is called
    #pragma warning disable CS8618
    public IConfigService ConfigService { get; set; }
    public IPluginManagementService PluginManagementService { get; set; }
    public ILoggerService LoggerService { get; set; }
    public IConsoleCommandsService ConsoleCommandsService { get; set; }
    #pragma warning restore CS8618

    public ContentPackage _package = null!;

    public Harmony? harmony;

    public void Initialize()
    {
        // When your plugin is loading, use this instead of the constructor for code relying on
        // the services above.

        if (PluginManagementService.TryGetPackageForPlugin<Plugin>(out var package))
        {
            _package = package;
        }

        harmony = new("shadowculling");
        harmony.PatchAll();

        LoadConfig();
        RegisterCommands();

        InitializeProjectSpecific();
    }

    public partial void InitializeProjectSpecific();

    public void OnLoadCompleted()
    {
        // After all plugins have loaded
        // Put code that interacts with other plugins here.
    }

    public void PreInitPatching()
    {
        // Called right after the constructor
    }

    public void Dispose()
    {
        // Cleanup your plugin!

        harmony?.UnpatchSelf();
        harmony = null;
    }

}