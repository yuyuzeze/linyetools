using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace KikuCaption.App.Behaviors;

/// <summary>
/// UI-R6B: keeps a window from opening (or being sized) larger than the monitor's work area at high
/// DPI (125% / 150%) or on small laptop screens. WPF sizes are in DIPs, so a fixed 820-DIP-tall
/// window becomes 1230 physical px at 150% — taller than a 1080p laptop's work area — pushing the
/// title bar or bottom controls off-screen where they cannot be dragged back. Attaching
/// <c>ClampToWorkArea="True"</c> caps the window's Max size to the current monitor's work area (in
/// DIPs), shrinks an over-large start size, and nudges it fully on-screen. It changes no content and
/// no business logic — only window geometry.
/// </summary>
public static class WindowSizing
{
    public static readonly DependencyProperty ClampToWorkAreaProperty =
        DependencyProperty.RegisterAttached(
            "ClampToWorkArea", typeof(bool), typeof(WindowSizing),
            new PropertyMetadata(false, OnClampToWorkAreaChanged));

    public static void SetClampToWorkArea(DependencyObject element, bool value)
        => element.SetValue(ClampToWorkAreaProperty, value);

    public static bool GetClampToWorkArea(DependencyObject element)
        => (bool)element.GetValue(ClampToWorkAreaProperty);

    private static void OnClampToWorkAreaChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Window window || e.NewValue is not true)
        {
            return;
        }

        // SourceInitialized runs after the HWND + DPI are known but before CenterScreen positioning,
        // so capping Max size here makes the initial placement use the clamped size (no visible jump).
        window.SourceInitialized += (_, _) => Clamp(window);
        // Loaded runs after positioning; re-nudge in case CenterScreen still placed part off-screen.
        window.Loaded += (_, _) => Clamp(window);
    }

    /// <summary>Caps the window to its monitor's work area (DIPs) and moves it fully on-screen.</summary>
    public static void Clamp(Window window)
    {
        if (!TryGetWorkAreaDip(window, out var area) || area.Width <= 0 || area.Height <= 0)
        {
            return;
        }

        // Never allow the window (start size OR user resize) to exceed the work area.
        window.MaxWidth = area.Width;
        window.MaxHeight = area.Height;

        double width = double.IsNaN(window.Width) ? window.ActualWidth : window.Width;
        double height = double.IsNaN(window.Height) ? window.ActualHeight : window.Height;
        // Before the window is placed, Left/Top are NaN — clamp size only, defer positioning.
        bool positioned = !double.IsNaN(window.Left) && !double.IsNaN(window.Top);
        double left = positioned ? window.Left : area.Left;
        double top = positioned ? window.Top : area.Top;

        var clamped = ComputeClampedBounds(area, new WindowBounds(left, top, width, height));

        if (clamped.Width < width) { window.Width = clamped.Width; }
        if (clamped.Height < height) { window.Height = clamped.Height; }
        if (positioned)
        {
            window.Left = clamped.Left;
            window.Top = clamped.Top;
        }
    }

    /// <summary>Window geometry in DIPs (pure value type for testable clamp math).</summary>
    public readonly record struct WindowBounds(double Left, double Top, double Width, double Height);

    /// <summary>
    /// Pure clamp: shrink a window to fit inside <paramref name="workAreaDip"/> and slide it so the
    /// whole window is on-screen. All values are DIPs. This is the DPI-independent core exercised by
    /// the 100% / 125% / 150% tests (paired with <see cref="WorkAreaPxToDip"/>).
    /// </summary>
    public static WindowBounds ComputeClampedBounds(Rect workAreaDip, WindowBounds desired)
    {
        double w = Math.Min(desired.Width, workAreaDip.Width);
        double h = Math.Min(desired.Height, workAreaDip.Height);
        double left = desired.Left;
        double top = desired.Top;

        if (left < workAreaDip.Left) { left = workAreaDip.Left; }
        if (top < workAreaDip.Top) { top = workAreaDip.Top; }
        if (left + w > workAreaDip.Right) { left = Math.Max(workAreaDip.Left, workAreaDip.Right - w); }
        if (top + h > workAreaDip.Bottom) { top = Math.Max(workAreaDip.Top, workAreaDip.Bottom - h); }

        return new WindowBounds(left, top, w, h);
    }

    /// <summary>Converts a physical-pixel work area to DIPs for a given DPI scale (1.0 / 1.25 / 1.5).</summary>
    public static Rect WorkAreaPxToDip(double leftPx, double topPx, double rightPx, double bottomPx, double dpiScale)
    {
        if (dpiScale <= 0) { dpiScale = 1.0; }
        return new Rect(leftPx / dpiScale, topPx / dpiScale,
            (rightPx - leftPx) / dpiScale, (bottomPx - topPx) / dpiScale);
    }

    // Work area of the monitor nearest the window, converted from physical px to DIPs using the
    // window's current DPI, so the result is correct at 100% / 125% / 150% and on multi-monitor.
    private static bool TryGetWorkAreaDip(Window window, out Rect areaDip)
    {
        areaDip = Rect.Empty;

        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
        {
            return false;
        }

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        // px -> DIP factor. TransformFromDevice.M11/M22 are 1/scale (e.g. 1/1.5 at 150%).
        double toDipX = 1.0, toDipY = 1.0;
        var source = PresentationSource.FromVisual(window);
        if (source?.CompositionTarget is { } ct)
        {
            var m = ct.TransformFromDevice;
            toDipX = m.M11;
            toDipY = m.M22;
        }

        var work = info.rcWork;
        areaDip = new Rect(
            work.left * toDipX,
            work.top * toDipY,
            (work.right - work.left) * toDipX,
            (work.bottom - work.top) * toDipY);
        return true;
    }

    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
}
