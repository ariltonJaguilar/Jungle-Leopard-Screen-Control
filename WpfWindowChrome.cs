using System.Runtime.InteropServices;

namespace JungleLeopardPoc;

internal static class Win11WindowChrome
{
    private const int UseImmersiveDarkMode = 20;
    private const int UseImmersiveDarkModeLegacy = 19;
    private const int BorderColor = 34;
    private const int CaptionColor = 35;
    private const int TextColor = 36;
    private const int WindowCornerPreference = 33;
    private const int DwmColor = unchecked((int)0xFFFFFFFE);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int valueSize);

    internal static void Apply(IntPtr window)
    {
        if (window == IntPtr.Zero) return;
        int darkMode = 1;
        if (DwmSetWindowAttribute(window, UseImmersiveDarkMode, ref darkMode, sizeof(int)) != 0)
            _ = DwmSetWindowAttribute(window, UseImmersiveDarkModeLegacy, ref darkMode, sizeof(int));
        int border = ToDwmColor(0x414047);
        int caption = ToDwmColor(0x201F23);
        int text = ToDwmColor(0xF3F1F5);
        int corners = 2;
        _ = DwmSetWindowAttribute(window, BorderColor, ref border, sizeof(int));
        _ = DwmSetWindowAttribute(window, CaptionColor, ref caption, sizeof(int));
        _ = DwmSetWindowAttribute(window, TextColor, ref text, sizeof(int));
        _ = DwmSetWindowAttribute(window, WindowCornerPreference, ref corners, sizeof(int));
    }

    private static int ToDwmColor(int rgb) => ((rgb & 0xff) << 16) | (rgb & 0xff00) | ((rgb >> 16) & 0xff);
}
