using System.Runtime.InteropServices;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using DrawingIcon = System.Drawing.Icon;

namespace JungleLeopardPoc;

internal sealed class NativeTrayIcon : IDisposable
{
    private const uint Add = 0x00000000;
    private const uint Delete = 0x00000002;
    private const uint Message = 0x00000001;
    private const uint Icon = 0x00000002;
    private const uint Tip = 0x00000004;
    private const int CallbackMessage = 0x8000 + 0x493;
    private const int LeftButtonDoubleClick = 0x0203;
    private const int RightButtonUp = 0x0205;
    private readonly IntPtr _windowHandle;
    private readonly HwndSource? _source;
    private readonly ContextMenu _menu;
    private readonly DrawingIcon? _trayIcon;
    private bool _visible;
    private bool _disposed;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int Size;
        public IntPtr Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr IconHandle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint VersionOrTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid ItemGuid;
        public IntPtr BalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr iconName);

    internal NativeTrayIcon(Window window, Action restore, Action exit)
    {
        _windowHandle = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_windowHandle);
        _source?.AddHook(WindowMessage);
        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico");
        try { _trayIcon = new DrawingIcon(iconPath); }
        catch (IOException) { }
        catch (ArgumentException) { }
        _menu = new ContextMenu
        {
            Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint
        };
        _menu.Items.Add(new MenuItem { Header = AppLanguage.Translate("Abrir Jungle Leopard Screen Control"), Command = new RelayCommand(restore) });
        _menu.Items.Add(new Separator());
        _menu.Items.Add(new MenuItem { Header = AppLanguage.Translate("Sair"), Command = new RelayCommand(exit) });
    }

    internal bool SetVisible(bool visible)
    {
        if (_disposed) return false;
        if (_visible == visible) return true;
        NotifyIconData data = CreateData();
        bool success = Shell_NotifyIcon(visible ? Add : Delete, ref data);
        if (success) _visible = visible;
        return success;
    }

    private NotifyIconData CreateData() => new()
    {
        Size = Marshal.SizeOf<NotifyIconData>(),
        Window = _windowHandle,
        Id = 1,
        Flags = Message | Icon | Tip,
        CallbackMessage = CallbackMessage,
        IconHandle = _trayIcon?.Handle ?? LoadIcon(IntPtr.Zero, (IntPtr)32512),
        Tip = AppLanguage.Translate("Jungle Leopard Screen Control"),
        Info = string.Empty,
        InfoTitle = string.Empty
    };

    private IntPtr WindowMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != CallbackMessage || unchecked((uint)wParam.ToInt64()) != 1) return IntPtr.Zero;
        int notification = unchecked((int)lParam.ToInt64());
        if (notification == LeftButtonDoubleClick)
        {
            _menu.IsOpen = false;
            RestoreRequested?.Invoke();
            handled = true;
        }
        else if (notification == RightButtonUp)
        {
            _menu.IsOpen = false;
            _menu.IsOpen = true;
            handled = true;
        }
        return IntPtr.Zero;
    }

    internal event Action? RestoreRequested;

    public void Dispose()
    {
        if (_disposed) return;
        SetVisible(false);
        _disposed = true;
        _source?.RemoveHook(WindowMessage);
        _menu.IsOpen = false;
        _trayIcon?.Dispose();
    }

    private sealed class RelayCommand(Action action) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => action();
    }
}
