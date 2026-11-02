using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PhoneAudioConnector;

/// <summary>Light/dark colors and the Win32 calls WinForms on .NET 8 lacks for dark mode.</summary>
internal sealed record Palette(
    bool IsDark,
    Color Back, // window background
    Color Surface, // list / log background
    Color Header, // list column header
    Color Text,
    Color Muted,
    Color Border,
    Color Accent,
    Color Good,
    Color Bad);

internal static class Theme
{
    private static readonly Palette Light = new(
        false,
        SystemColors.Control,
        SystemColors.Window,
        SystemColors.Control,
        SystemColors.WindowText,
        SystemColors.GrayText,
        SystemColors.ControlDark,
        Color.FromArgb(29, 78, 216),
        Color.FromArgb(21, 128, 61),
        Color.FromArgb(185, 28, 28));

    private static readonly Palette Dark = new(
        true,
        Color.FromArgb(32, 32, 32),
        Color.FromArgb(43, 43, 43),
        Color.FromArgb(38, 38, 38),
        Color.FromArgb(240, 240, 240),
        Color.FromArgb(160, 160, 160),
        Color.FromArgb(70, 70, 70),
        Color.FromArgb(96, 165, 250),
        Color.FromArgb(74, 222, 128),
        Color.FromArgb(248, 113, 113));

    /// <summary>Follows Settings > Personalization > Colors > "Choose your app mode".</summary>
    public static Palette Current => SystemUsesDarkMode() ? Dark : Light;

    private static bool SystemUsesDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is 0;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? appName, string? idList);

    /// <summary>Dark title bar. Attribute 20 on Win10 20H1+/Win11, 19 on older Win10 builds.</summary>
    public static void ApplyTitleBar(Form form, bool dark)
    {
        if (!form.IsHandleCreated) return;
        var value = dark ? 1 : 0;
        if (DwmSetWindowAttribute(form.Handle, 20, ref value, sizeof(int)) != 0)
            DwmSetWindowAttribute(form.Handle, 19, ref value, sizeof(int));
    }

    /// <summary>Dark scrollbars on native controls (ListView, multiline TextBox).</summary>
    public static void ApplyScrollbars(Control control, bool dark)
    {
        if (!control.IsHandleCreated) return;
        SetWindowTheme(control.Handle, dark ? "DarkMode_Explorer" : "Explorer", null);
    }

    /// <summary>Colors for the tray context menu in dark mode.</summary>
    public sealed class DarkMenuColors : ProfessionalColorTable
    {
        private static readonly Color Bg = Color.FromArgb(43, 43, 43);
        private static readonly Color Hover = Color.FromArgb(65, 65, 65);
        private static readonly Color Line = Color.FromArgb(70, 70, 70);

        public override Color ToolStripDropDownBackground => Bg;
        public override Color ImageMarginGradientBegin => Bg;
        public override Color ImageMarginGradientMiddle => Bg;
        public override Color ImageMarginGradientEnd => Bg;
        public override Color MenuBorder => Line;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color SeparatorDark => Line;
        public override Color SeparatorLight => Line;
    }
}