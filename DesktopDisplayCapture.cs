using System.Drawing;
using System.Runtime.InteropServices;

namespace JungleLeopardPoc;

internal sealed record DesktopDisplayInfo(string DeviceName, Rectangle Bounds);

internal static class DesktopDisplayCapture
{
    private const uint CursorShowing = 0x00000001;
    private const uint DiNormal = 0x0003;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo { public int Size; public uint Flags; public IntPtr Cursor; public NativePoint Position; }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo { [MarshalAs(UnmanagedType.Bool)] public bool IsIcon; public uint HotspotX, HotspotY; public IntPtr Mask, Color; }

    private delegate bool MonitorCallback(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorCallback callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CursorInfo info);
    [DllImport("user32.dll")]
    private static extern bool GetIconInfo(IntPtr cursor, out IconInfo info);
    [DllImport("user32.dll")]
    private static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int width, int height, uint frame, IntPtr brush, uint flags);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr value);

    internal static IReadOnlyList<DesktopDisplayInfo> Enumerate()
    {
        var displays = new List<DesktopDisplayInfo>();
        _ = EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                var bounds = new Rectangle(info.Monitor.Left, info.Monitor.Top,
                    info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top);
                displays.Add(new DesktopDisplayInfo(info.Device.TrimEnd('\0'), bounds));
            }
            return true;
        }, IntPtr.Zero);
        return displays;
    }

    internal static void DrawCursor(Graphics graphics, Rectangle monitorBounds)
    {
        var info = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        if (!GetCursorInfo(ref info) || (info.Flags & CursorShowing) == 0 || info.Cursor == IntPtr.Zero) return;
        if (!GetIconInfo(info.Cursor, out IconInfo iconInfo)) return;
        try
        {
            int x = info.Position.X - monitorBounds.Left - (int)iconInfo.HotspotX;
            int y = info.Position.Y - monitorBounds.Top - (int)iconInfo.HotspotY;
            IntPtr dc = graphics.GetHdc();
            try { _ = DrawIconEx(dc, x, y, info.Cursor, 0, 0, 0, IntPtr.Zero, DiNormal); }
            finally { graphics.ReleaseHdc(dc); }
        }
        finally
        {
            if (iconInfo.Mask != IntPtr.Zero) _ = DeleteObject(iconInfo.Mask);
            if (iconInfo.Color != IntPtr.Zero) _ = DeleteObject(iconInfo.Color);
        }
    }
}
