using Microsoft.Win32;

namespace SuperScreenshot;

/// <summary>User preferences, in HKCU\Software\SuperScreenshot (removed by the uninstaller).</summary>
internal static class Settings
{
    const string Key = @"Software\SuperScreenshot";

    /// <summary>
    /// Open the preview window after every capture, instead of the small popup in the corner.
    /// Off by default, like Snipping Tool: the popup doesn't take the focus, so you can paste
    /// straight away.
    /// </summary>
    public static bool OpenPreviewAfterCapture
    {
        get => GetInt("OpenPreviewAfterCapture") == 1;
        set => SetInt("OpenPreviewAfterCapture", value ? 1 : 0);
    }

    /// <summary>True when Windows apps are set to the dark theme.</summary>
    public static bool WindowsUsesDarkTheme
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
    }

    static int GetInt(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(Key);
        return key?.GetValue(name) is int value ? value : 0;
    }

    static void SetInt(string name, int value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Key);
        key.SetValue(name, value, RegistryValueKind.DWord);
    }
}
