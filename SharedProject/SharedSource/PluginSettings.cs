namespace ShadowCulling;

public partial class Plugin
{
    private static BooleanSetting? _cullingEnabledSetting;
    public static bool CullingEnabled
    {
        get => _cullingEnabledSetting!.Value;
        set => _cullingEnabledSetting!.Set(value);
    }

    private static FloatSetting? _cullingIntervalSetting;
    public static float CullingInterval
    {
        get => _cullingIntervalSetting!.Value;
        set => _cullingIntervalSetting!.Set(value);
    }

    private static BooleanSetting? _debugLoggingEnabledSetting;
    public static bool DebugLoggingEnabled
    {
        get => _debugLoggingEnabledSetting!.Value;
        set => _debugLoggingEnabledSetting!.Set(value);
    }

    private static BooleanSetting? _debugDrawingEnabledSetting;
    public static bool DebugDrawingEnabled
    {
        get => _debugDrawingEnabledSetting!.Value;
        set => _debugDrawingEnabledSetting!.Set(value);
    }

    private static BooleanSetting? _debugDrawingHullSetting;
    public static bool DebugDrawingHull
    {
        get => _debugDrawingHullSetting!.Value;
        set => _debugDrawingHullSetting!.Set(value);
    }

    private static BooleanSetting? _debugDrawingShadowSetting;
    public static bool DebugDrawingShadow
    {
        get => _debugDrawingShadowSetting!.Value;
        set => _debugDrawingShadowSetting!.Set(value);
    }

    private static FloatSetting? _debugDrawingShadowLengthSetting;
    public static float DebugDrawingShadowLength
    {
        get => _debugDrawingShadowLengthSetting!.Value;
        set => _debugDrawingShadowLengthSetting!.Set(value);
    }

    private static BooleanSetting? _debugDrawingItemSetting;
    public static bool DebugDrawingItem
    {
        get => _debugDrawingItemSetting!.Value;
        set => _debugDrawingItemSetting!.Set(value);
    }

    private static BooleanSetting? _debugDrawingStructureSetting;
    public static bool DebugDrawingStructure
    {
        get => _debugDrawingStructureSetting!.Value;
        set => _debugDrawingStructureSetting!.Set(value);
    }

    private static BooleanSetting? _debugDrawingCharacterSetting;
    public static bool DebugDrawingCharacter
    {
        get => _debugDrawingCharacterSetting!.Value;
        set => _debugDrawingCharacterSetting!.Set(value);
    }

    private void CreateSettings()
    {
        SettingsService.OverrideUI = contentList =>
        {
            foreach (var setting in SettingsService.RegisteredSettings)
            {
                if (setting.ShowInUI)
                {
                    setting.AddUI(contentList);
                }

                switch (setting)
                {
                    case BooleanSetting booleanSetting:
                        if (booleanSetting.Identifier == "cullingenabled")
                        {
                            booleanSetting.TickBox!.OnSelected += tickBox =>
                            {
                                if (!CullingEnabled)
                                {
                                    TryClearAll();
                                }
                                return true;
                            };
                        }
                        break;
                    default:
                        break;
                }
            }
        };

        RegisterBooleanSetting("cullingenabled", true, out _cullingEnabledSetting);

        RegisterFloatSetting("cullinginterval", 0.05f, out _cullingIntervalSetting, setting =>
        {
            setting.UseSlider = true;
            setting.LabelFunc = x => $"{MathF.Round(x, 2)}s";
            setting.StepValue = 0.01f;
            setting.Range = (0f, 0.2f);
        });

        RegisterBooleanSetting("debugloggingenabled", false, out _debugLoggingEnabledSetting);
        RegisterBooleanSetting("debugdrawingenabled", false, out _debugDrawingEnabledSetting);
        RegisterBooleanSetting("debugdrawinghull", false, out _debugDrawingHullSetting);

        RegisterBooleanSetting("debugdrawingshadow", true, out _debugDrawingShadowSetting);

        RegisterFloatSetting("debugdrawingshadowlength", 300.0f, out _debugDrawingShadowLengthSetting, setting =>
        {
            setting.UseSlider = true;
            setting.LabelFunc = x => $"{MathF.Round(x)} pixels";
            setting.StepValue = 100f;
            setting.Range = (0f, 10000f);
        });

        RegisterBooleanSetting("debugdrawingitem", true, out _debugDrawingItemSetting);
        RegisterBooleanSetting("debugdrawingstructure", true, out _debugDrawingStructureSetting);
        RegisterBooleanSetting("debugdrawingcharacter", true, out _debugDrawingCharacterSetting);
    }

    private static void RegisterBooleanSetting(
        string identifier,
        bool defaultValue,
        out BooleanSetting setting,
        Action<BooleanSetting>? configure = null)
    {
        setting = new BooleanSetting(identifier.ToIdentifier(), defaultValue, label: $"{ModPrefix}.{identifier}.displayname")
        {
            ShowInUI = true,
            ToolTip = $"{ModPrefix}.{identifier}.tooltip",
            SyncMode = SettingSyncMode.NoSync
        };
        configure?.Invoke(setting);
        SettingsService.RegisterSetting(setting);
    }

    private static void RegisterFloatSetting(
        string identifier,
        float defaultValue,
        out FloatSetting setting,
        Action<FloatSetting>? configure = null)
    {
        setting = new FloatSetting(identifier.ToIdentifier(), defaultValue, label: $"{ModPrefix}.{identifier}.displayname")
        {
            ShowInUI = true,
            ToolTip = $"{ModPrefix}.{identifier}.tooltip",
            SyncMode = SettingSyncMode.NoSync
        };
        configure?.Invoke(setting);
        SettingsService.RegisterSetting(setting);
    }
}