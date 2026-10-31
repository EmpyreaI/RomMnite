using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace RomM.LibraryHome
{
    /// <summary>
    /// How much wider a window would be maximized: its screen's work area (the taskbar left
    /// out), in WPF's units, less the window's own width now. Zero when maximized, or when it
    /// cannot be told.
    /// </summary>
    internal static class ScreenWidth
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            public int Size;
            public NativeRect Monitor;
            public NativeRect Work;
            public uint Flags;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

        public static double MissingFromMaximized(Window window)
        {
            if (window == null || window.WindowState == WindowState.Maximized)
                return 0;
            try
            {
                double work = SystemParameters.WorkArea.Width;
                IntPtr handle = new WindowInteropHelper(window).Handle;
                if (handle != IntPtr.Zero)
                {
                    IntPtr monitor = MonitorFromWindow(handle, 2 /* MONITOR_DEFAULTTONEAREST */);
                    var info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
                    if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
                    {
                        double scale = 1.0;
                        PresentationSource source = PresentationSource.FromVisual(window);
                        if (source?.CompositionTarget != null)
                            scale = source.CompositionTarget.TransformToDevice.M11;
                        work = (info.Work.Right - info.Work.Left) / scale;
                    }
                }
                return Math.Max(0, work - window.ActualWidth);
            }
            catch
            {
                return 0;
            }
        }
    }
}
