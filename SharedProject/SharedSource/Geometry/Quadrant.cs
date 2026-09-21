namespace ShadowCulling;

/// <summary>Defines the four quadrants and combinations for spatial partitioning.</summary>
[Flags]
public enum Quadrant
{
    None = 0,
    RightTop = 0x01,
    LeftTop = 0x02,
    LeftBottom = 0x04,
    RightBottom = 0x08,
    Top = RightTop | LeftTop,
    Left = LeftTop | LeftBottom,
    Bottom = RightBottom | LeftBottom,
    Right = RightTop | RightBottom,
    All = RightTop | LeftTop | LeftBottom | RightBottom,
}
