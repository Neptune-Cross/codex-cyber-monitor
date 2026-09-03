using System.Runtime.InteropServices;

namespace CodexCyberMonitor.Infrastructure;

internal static class NativeMethods
{
    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const int SwRestore = 9;
    private const uint FlashwAll = 0x00000003;
    private const uint FlashwTimerNoForeground = 0x0000000C;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr windowHandle, int command);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashWindowInfo info);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyIcon(IntPtr iconHandle);

    public static void BringAlertToFront(Form form)
    {
        if (!form.IsHandleCreated)
        {
            return;
        }

        ShowWindowAsync(form.Handle, SwRestore);
        SetWindowPos(
            form.Handle,
            HwndTopmost,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpShowWindow);
        SetForegroundWindow(form.Handle);

        var flashInfo = new FlashWindowInfo
        {
            Size = (uint)Marshal.SizeOf<FlashWindowInfo>(),
            WindowHandle = form.Handle,
            Flags = FlashwAll | FlashwTimerNoForeground,
            Count = 8,
            Timeout = 0
        };
        FlashWindowEx(ref flashInfo);
    }

    public static void EnsureTopmost(Form form)
    {
        if (!form.IsHandleCreated)
        {
            return;
        }

        SetWindowPos(
            form.Handle,
            HwndTopmost,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashWindowInfo
    {
        public uint Size;
        public IntPtr WindowHandle;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }
}
