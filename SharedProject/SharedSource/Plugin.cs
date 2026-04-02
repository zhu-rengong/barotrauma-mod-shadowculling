using Barotrauma;
using Barotrauma.Plugins;
using HarmonyLib;
using Microsoft.Xna.Framework;
using System.Runtime.CompilerServices;


#if CLIENT
[assembly: IgnoresAccessChecksTo("Barotrauma")]
#endif
#if SERVER
[assembly: IgnoresAccessChecksTo("DedicatedServer")]
#endif
[assembly: IgnoresAccessChecksTo("BarotraumaCore")]

namespace ShadowCulling;

/// <summary>
/// Main plugin class for shadow culling functionality in Barotrauma.
/// Implements <see cref="IBarotraumaPlugin"/> for integration with the game.
/// </summary>
public partial class Plugin : IBarotraumaPlugin
{
    public static readonly IDebugConsole DebugConsole = PluginServiceProvider.GetService<IDebugConsole>();
    public static readonly ISettingsService SettingsService = PluginServiceProvider.GetService<ISettingsService>();
    public static readonly IHarmonyProvider HarmonyProvider = PluginServiceProvider.GetService<IHarmonyProvider>();

    // Settings properties
    public static bool CullingEnabled => SettingsService.RetrieveSetting<BooleanSetting>("shadowculling.cullingenabled".ToIdentifier()) is BooleanSetting { Value: true };
    public static bool DebugLoggingEnabled => SettingsService.RetrieveSetting<BooleanSetting>("shadowculling.debuglog".ToIdentifier()) is BooleanSetting { Value: true };
    public static bool DebugDrawingEnabled => SettingsService.RetrieveSetting<BooleanSetting>("shadowculling.debugdraw".ToIdentifier()) is BooleanSetting { Value: true };
    public static bool DebugDrawingHull => SettingsService.RetrieveSetting<BooleanSetting>("shadowculling.debugdrawhull".ToIdentifier()) is BooleanSetting { Value: true };
    public static bool DebugDrawingShadow => SettingsService.RetrieveSetting<BooleanSetting>("shadowculling.debugdrawshadow".ToIdentifier()) is BooleanSetting { Value: true };
    public static float DebugDrawingShadowLength => SettingsService.RetrieveSetting<FloatSetting>("shadowculling.debugdrawshadowlength".ToIdentifier())?.Value ?? 0.0f;
    public static bool DebugDrawingItem => SettingsService.RetrieveSetting<BooleanSetting>("shadowculling.debugdrawitem".ToIdentifier()) is BooleanSetting { Value: true };
    public static bool DebugDrawingStructure => SettingsService.RetrieveSetting<BooleanSetting>("shadowculling.debugdrawstructure".ToIdentifier()) is BooleanSetting { Value: true };
    public static bool DebugDrawingCharacter => SettingsService.RetrieveSetting<BooleanSetting>("shadowculling.debugdrawcharacter".ToIdentifier()) is BooleanSetting { Value: true };

    /// <summary>
    /// Initializes the plugin. Called when the plugin is loaded.
    /// </summary>
    public void Init()
    {
        DebugConsole.NewMessage("Plugin loaded", Color.Lime);

        InitializeProjectSpecific();

        HarmonyProvider.PatchAll();

        CreateSettings();

        RegisterCommands();
    }

    /// <summary>
    /// Platform/Project-specific initialization method.
    /// </summary>
    public partial void InitializeProjectSpecific();

    /// <summary>
    /// Called when all game content has been loaded.
    /// </summary>
    public void OnContentLoaded()
    {
        // Reserved for future content loading logic
    }

    /// <summary>
    /// Called when the plugin is being disposed.
    /// </summary>
    public void Dispose()
    {
        DebugConsole.NewMessage("Plugin unloaded", Color.Red);
    }

    public static void CreateSettings()
    {
        ISettingsService settingsService = Plugin.SettingsService;
        IDebugConsole debugConsole = Plugin.DebugConsole;

        settingsService.RegisterSetting(new BooleanSetting("shadowculling.cullingenabled".ToIdentifier(), true, label: "Enable shadow culling")
        {
            ShowInUI = true,
            SyncMode = SettingSyncMode.NoSync
        });

        settingsService.RegisterSetting(new BooleanSetting("shadowculling.debuglog".ToIdentifier(), false, label: "Is debug logging enabled?")
        {
            ShowInUI = true,
            SyncMode = SettingSyncMode.NoSync
        });

        settingsService.RegisterSetting(new BooleanSetting("shadowculling.debugdraw".ToIdentifier(), false, label: "Is debug drawing enabled?")
        {
            ShowInUI = true,
            SyncMode = SettingSyncMode.NoSync
        });

        settingsService.RegisterSetting(new BooleanSetting("shadowculling.debugdrawhull".ToIdentifier(), false, label: "During debug drawing, should hulls be drawn?")
        {
            ShowInUI = true,
            SyncMode = SettingSyncMode.NoSync
        });

        settingsService.RegisterSetting(new BooleanSetting("shadowculling.debugdrawshadow".ToIdentifier(), true, label: "During debug drawing, should shadows be drawn?")
        {
            ShowInUI = true,
            SyncMode = SettingSyncMode.NoSync
        });

        settingsService.RegisterSetting(new FloatSetting(
            "shadowculling.debugdrawshadowlength".ToIdentifier(),
            300.0f,
            label: "During debug drawing, the length of shadow rays (in pixels).")
        {
            ShowInUI = true,
            UseSlider = true,
            LabelFunc = (x) => $"{MathF.Round(x)} pixels",
            StepValue = 100f,
            Range = (0f, 10000f),
            SyncMode = SettingSyncMode.NoSync
        });

        settingsService.RegisterSetting(new BooleanSetting("shadowculling.debugdrawitem".ToIdentifier(), true, label: "During debug drawing, should items be drawn?")
        {
            ShowInUI = true,
            SyncMode = SettingSyncMode.NoSync
        });

        settingsService.RegisterSetting(new BooleanSetting("shadowculling.debugdrawstructure".ToIdentifier(), true, label: "During debug drawing, should structures be drawn?")
        {
            ShowInUI = true,
            SyncMode = SettingSyncMode.NoSync
        });

        settingsService.RegisterSetting(new BooleanSetting("shadowculling.debugdrawcharacter".ToIdentifier(), true, label: "During debug drawing, should characters be drawn?")
        {
            ShowInUI = true,
            SyncMode = SettingSyncMode.NoSync
        });

        debugConsole.RegisterCommand(
            command: "shadowcullingtoggle",
            helpMessage: "Toggles shadow culling on/off",
            flags: CommandFlags.DoNotRelayToServer,
            onCommandExecuted: (string[] args) =>
            {
                if (settingsService?.RetrieveSetting<BooleanSetting>("shadowculling.cullingenabled".ToIdentifier()) is BooleanSetting cullingEnabled)
                {
                    ToggleCulling(cullingEnabled);
                }
            });

        debugConsole.RegisterCommand(
            command: "shadowcullingdebugdraw",
            helpMessage: "Toggles debug drawing on/off",
            flags: CommandFlags.DoNotRelayToServer,
            onCommandExecuted: (string[] args) =>
            {
                if (settingsService?.RetrieveSetting<BooleanSetting>("shadowculling.debugdraw".ToIdentifier()) is BooleanSetting debugDrawing)
                {
                    debugDrawing.Set(!debugDrawing.Value);
                }
            });
    }

    /// <summary>
    /// Toggles the culling state and clears all data if disabled.
    /// </summary>
    private static void ToggleCulling(BooleanSetting cullingSetting)
    {
        cullingSetting.Set(!cullingSetting.Value);

        if (!cullingSetting.Value)
        {
            Plugin.TryClearAll();
        }
    }

    private void RegisterCommands()
    {
        DebugConsole.RegisterCommand(
            command: "shadowcullingdebugonce",
            helpMessage: "Performs a single debug culling operation",
            flags: CommandFlags.DoNotRelayToServer,
            onCommandExecuted: (string[] args) =>
            {
                TryClearAll();
                PerformEntityCulling(debug: true);
            });
    }
}