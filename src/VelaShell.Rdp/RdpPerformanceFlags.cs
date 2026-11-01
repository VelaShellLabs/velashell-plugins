namespace VelaShell.Rdp;

/// <summary>
/// Desktop-experience flags sent in the Client Info PDU (MS-RDPBCGR 2.2.1.11.1.1.1 performanceFlags).
/// Each <c>Disable*</c> bit turns a feature off to save bandwidth; each <c>Enable*</c> bit turns one on.
/// Combine the features to suppress; the default favours responsiveness over fidelity.
/// </summary>
[Flags]
public enum RdpPerformanceFlags : uint
{
    /// <summary>Keep every desktop feature (wallpaper, animations, themes …).</summary>
    None = 0x000,

    /// <summary>PERF_DISABLE_WALLPAPER: hide the desktop background.</summary>
    DisableWallpaper = 0x001,

    /// <summary>PERF_DISABLE_FULLWINDOWDRAG: show only an outline while dragging a window.</summary>
    DisableFullWindowDrag = 0x002,

    /// <summary>PERF_DISABLE_MENUANIMATIONS: turn off menu and window animations.</summary>
    DisableMenuAnimations = 0x004,

    /// <summary>PERF_DISABLE_THEME: use the classic (non-themed) visual style.</summary>
    DisableThemes = 0x008,

    /// <summary>PERF_DISABLE_CURSOR_SHADOW: drop the pointer shadow.</summary>
    DisableCursorShadow = 0x020,

    /// <summary>PERF_DISABLE_CURSORSETTINGS: disable cursor blink.</summary>
    DisableCursorSettings = 0x040,

    /// <summary>PERF_ENABLE_FONT_SMOOTHING: render text with ClearType.</summary>
    EnableFontSmoothing = 0x080,

    /// <summary>PERF_ENABLE_DESKTOP_COMPOSITION: allow desktop composition (DWM effects).</summary>
    EnableDesktopComposition = 0x100,
}
