using Barotrauma.LuaCs.Data;
using System.Diagnostics.CodeAnalysis;

namespace ShadowCulling;

public partial class Plugin
{
    private static ISettingBase<bool> _cullingEnabledSetting = null!;
    public static bool CullingEnabled
    {
        get => _cullingEnabledSetting.Value;
        set => _cullingEnabledSetting.TrySetValue(value);
    }

    private static ISettingRangeBase<float> _cullingIntervalSetting = null!;
    public static float CullingInterval
    {
        get => _cullingIntervalSetting.Value;
        set => _cullingIntervalSetting.TrySetValue(value);
    }

    private static ISettingBase<bool> _debugLoggingEnabledSetting = null!;
    public static bool DebugLoggingEnabled
    {
        get => _debugLoggingEnabledSetting.Value;
        set => _debugLoggingEnabledSetting.TrySetValue(value);
    }

    private static ISettingBase<bool> _debugDrawingEnabledSetting = null!;
    public static bool DebugDrawingEnabled
    {
        get => _debugDrawingEnabledSetting.Value;
        set => _debugDrawingEnabledSetting.TrySetValue(value);
    }

    private static ISettingBase<bool> _debugDrawingHullSetting = null!;
    public static bool DebugDrawingHull
    {
        get => _debugDrawingHullSetting.Value;
        set => _debugDrawingHullSetting.TrySetValue(value);
    }

    private static ISettingBase<bool> _debugDrawingShadowSetting = null!;
    public static bool DebugDrawingShadow
    {
        get => _debugDrawingShadowSetting.Value;
        set => _debugDrawingShadowSetting.TrySetValue(value);
    }

    private static ISettingRangeBase<float> _debugDrawingShadowLengthSetting = null!;
    public static float DebugDrawingShadowLength
    {
        get => _debugDrawingShadowLengthSetting.Value;
        set => _debugDrawingShadowLengthSetting.TrySetValue(value);
    }

    private static ISettingBase<bool> _debugDrawingItemSetting = null!;
    public static bool DebugDrawingItem
    {
        get => _debugDrawingItemSetting.Value;
        set => _debugDrawingItemSetting.TrySetValue(value);
    }

    private static ISettingBase<bool> _debugDrawingStructureSetting = null!;
    public static bool DebugDrawingStructure
    {
        get => _debugDrawingStructureSetting.Value;
        set => _debugDrawingStructureSetting.TrySetValue(value);
    }

    private static ISettingBase<bool> _debugDrawingCharacterSetting = null!;
    public static bool DebugDrawingCharacter
    {
        get => _debugDrawingCharacterSetting.Value;
        set => _debugDrawingCharacterSetting.TrySetValue(value);
    }

    private void LoadConfig()
    {
        if (TryGetConfig("CullingEnabled", out _cullingEnabledSetting))
        {
            _cullingEnabledSetting.OnValueChanged += setting =>
            {
                if (!CullingEnabled)
                {
                    TryClearAll();
                }
            };
        }

        TryGetConfig("CullingInterval", out _cullingIntervalSetting);
        TryGetConfig("DebugLoggingEnabled", out _debugLoggingEnabledSetting);
        TryGetConfig("DebugDrawingEnabled", out _debugDrawingEnabledSetting);
        TryGetConfig("DebugDrawingHull", out _debugDrawingHullSetting);
        TryGetConfig("DebugDrawingShadow", out _debugDrawingShadowSetting);
        TryGetConfig("DebugDrawingShadowLength", out _debugDrawingShadowLengthSetting);
        TryGetConfig("DebugDrawingItem", out _debugDrawingItemSetting);
        TryGetConfig("DebugDrawingStructure", out _debugDrawingStructureSetting);
        TryGetConfig("DebugDrawingCharacter", out _debugDrawingCharacterSetting);
    }

    private bool TryGetConfig<T>(string name, [NotNullWhen(true)] out T setting) where T : ISettingBase
    {
        if (!ConfigService.TryGetConfig(_package, name, out setting))
        {
            LoggerService.LogError($"Failed to find config named {name}!");
            return false;
        }

        return true;
    }
}