using Barotrauma.LuaCs.Data;

namespace ShadowCulling;

public partial class Plugin
{
    private static ISettingBase<bool>? _cullingEnabledSetting;
    public static bool CullingEnabled
    {
        get => _cullingEnabledSetting?.Value ?? false;
        set => _cullingEnabledSetting?.TrySetValue(value);
    }

    private static ISettingRangeBase<float>? _cullingIntervalSetting;
    public static float CullingInterval
    {
        get => _cullingIntervalSetting?.Value ?? 0.15f;
        set => _cullingIntervalSetting?.TrySetValue(value);
    }

    private static ISettingBase<bool>? _debugLoggingEnabledSetting;
    public static bool DebugLoggingEnabled
    {
        get => _debugLoggingEnabledSetting?.Value ?? false;
        set => _debugLoggingEnabledSetting?.TrySetValue(value);
    }

    private static ISettingBase<bool>? _debugDrawingEnabledSetting;
    public static bool DebugDrawingEnabled
    {
        get => _debugDrawingEnabledSetting?.Value ?? false;
        set => _debugDrawingEnabledSetting?.TrySetValue(value);
    }

    private static ISettingBase<bool>? _debugDrawingHullSetting;
    public static bool DebugDrawingHull
    {
        get => _debugDrawingHullSetting?.Value ?? false;
        set => _debugDrawingHullSetting?.TrySetValue(value);
    }

    private static ISettingBase<bool>? _debugDrawingShadowSetting;
    public static bool DebugDrawingShadow
    {
        get => _debugDrawingShadowSetting?.Value ?? false;
        set => _debugDrawingShadowSetting?.TrySetValue(value);
    }

    private static ISettingRangeBase<float>? _debugDrawingShadowLengthSetting;
    public static float DebugDrawingShadowLength
    {
        get => _debugDrawingShadowLengthSetting?.Value ?? 0.15f;
        set => _debugDrawingShadowLengthSetting?.TrySetValue(value);
    }

    private static ISettingBase<bool>? _debugDrawingItemSetting;
    public static bool DebugDrawingItem
    {
        get => _debugDrawingItemSetting?.Value ?? false;
        set => _debugDrawingItemSetting?.TrySetValue(value);
    }

    private static ISettingBase<bool>? _debugDrawingStructureSetting;
    public static bool DebugDrawingStructure
    {
        get => _debugDrawingStructureSetting?.Value ?? false;
        set => _debugDrawingStructureSetting?.TrySetValue(value);
    }

    private static ISettingBase<bool>? _debugDrawingCharacterSetting;
    public static bool DebugDrawingCharacter
    {
        get => _debugDrawingCharacterSetting?.Value ?? false;
        set => _debugDrawingCharacterSetting?.TrySetValue(value);
    }

    private void LoadConfig()
    {
        {
            _cullingEnabledSetting = ConfigService.TryGetConfig<ISettingBase<bool>>(_package, "CullingEnabled", out var val) ? val : null;
            if (_cullingEnabledSetting is not null)
            {
                _cullingEnabledSetting.OnValueChanged += setting =>
                {
                    if (!CullingEnabled)
                    {
                        TryClearAll();
                    }
                };
            }
        }
        { _cullingIntervalSetting = ConfigService.TryGetConfig<ISettingRangeBase<float>>(_package, "CullingInterval", out var val) ? val : null; }
        { _debugLoggingEnabledSetting = ConfigService.TryGetConfig<ISettingBase<bool>>(_package, "DebugLoggingEnabled", out var val) ? val : null; }
        { _debugDrawingEnabledSetting = ConfigService.TryGetConfig<ISettingBase<bool>>(_package, "DebugDrawingEnabled", out var val) ? val : null; }
        { _debugDrawingHullSetting = ConfigService.TryGetConfig<ISettingBase<bool>>(_package, "DebugDrawingHull", out var val) ? val : null; }
        { _debugDrawingShadowSetting = ConfigService.TryGetConfig<ISettingBase<bool>>(_package, "DebugDrawingShadow", out var val) ? val : null; }
        { _debugDrawingShadowLengthSetting = ConfigService.TryGetConfig<ISettingRangeBase<float>>(_package, "DebugDrawingShadowLength", out var val) ? val : null; }
        { _debugDrawingItemSetting = ConfigService.TryGetConfig<ISettingBase<bool>>(_package, "DebugDrawingItem", out var val) ? val : null; }
        { _debugDrawingStructureSetting = ConfigService.TryGetConfig<ISettingBase<bool>>(_package, "DebugDrawingStructure", out var val) ? val : null; }
        { _debugDrawingCharacterSetting = ConfigService.TryGetConfig<ISettingBase<bool>>(_package, "DebugDrawingCharacter", out var val) ? val : null; }
    }
}