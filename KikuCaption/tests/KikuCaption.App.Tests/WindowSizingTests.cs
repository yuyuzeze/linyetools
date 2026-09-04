using System.Windows;
using KikuCaption.App.Behaviors;
using Xunit;

namespace KikuCaption.App.Tests;

/// <summary>
/// UI-R6B: the DPI-independent window-clamp core. A WPF window's Width/Height are in DIPs, so a fixed
/// design size becomes larger in physical pixels as the DPI scale rises (125% / 150%). These tests
/// drive the pure clamp math with each monitor's real physical work area converted to DIPs at 100%,
/// 125% and 150%, proving the window is shrunk to fit and slid fully on-screen. The MainWindow design
/// size is 1180 x 760 DIP (min 720 x 480); MeetingPlaybackWindow is 1120 x 720 DIP.
/// </summary>
public class WindowSizingTests
{
    // A 1080p laptop panel (1920x1080), taskbar ~48 px → work area 1920 x 1032 physical px.
    private const double LaptopW = 1920, LaptopH = 1032;

    private static Rect WorkDip(double scale) => WindowSizing.WorkAreaPxToDip(0, 0, LaptopW, LaptopH, scale);

    [Theory] // MainWindow (1180x760 DIP) on a 1080p laptop at each scale
    [InlineData(1.00, 1180, 760)] // 100%: work 1920x1032 DIP → fits as-is
    [InlineData(1.25, 1180, 760)] // 125%: work 1536x825  DIP → height 760 fits, width 1180 fits
    [InlineData(1.50, 1180, 688)] // 150%: work 1280x688  DIP → height clamped 760→688
    public void MainWindow_ClampedToLaptopWorkArea(double scale, double expectW, double expectH)
    {
        var work = WorkDip(scale);
        var desired = new WindowSizing.WindowBounds(370, 130, 1180, 760); // CenterScreen-ish start

        var r = WindowSizing.ComputeClampedBounds(work, desired);

        Assert.True(r.Width <= work.Width + 0.01);
        Assert.True(r.Height <= work.Height + 0.01);
        Assert.Equal(expectW, r.Width, 0);
        Assert.Equal(expectH, r.Height, 0);
        // Fully on-screen after clamping.
        Assert.True(r.Left >= work.Left - 0.01 && r.Left + r.Width <= work.Right + 0.01);
        Assert.True(r.Top >= work.Top - 0.01 && r.Top + r.Height <= work.Bottom + 0.01);
    }

    [Theory] // At 150% the taller playback (720) and main (760) windows both must shrink to 688 DIP
    [InlineData(760)]
    [InlineData(720)]
    public void TallWindows_At150_ShrinkToWorkHeight(double designHeight)
    {
        var work = WorkDip(1.50); // 1280 x 688 DIP
        var r = WindowSizing.ComputeClampedBounds(work, new WindowSizing.WindowBounds(0, 0, 1120, designHeight));
        Assert.Equal(688, r.Height, 0);
        Assert.True(r.Top + r.Height <= work.Bottom + 0.01);
    }

    [Fact] // A CenterScreen placement that pushes the window off the bottom is slid back on-screen
    public void OffScreenPlacement_SlidFullyOnScreen()
    {
        var work = WorkDip(1.50); // 1280 x 688
        // Window placed near the bottom so it would overflow before clamping.
        var r = WindowSizing.ComputeClampedBounds(work, new WindowSizing.WindowBounds(200, 600, 900, 500));
        Assert.True(r.Left + r.Width <= work.Right + 0.01);
        Assert.True(r.Top + r.Height <= work.Bottom + 0.01);
        Assert.True(r.Left >= 0 && r.Top >= 0);
    }

    [Fact] // A tiny netbook screen (1366x768 @125% → 1092x566 DIP) clamps both dimensions
    public void SmallScreen_ClampsBothDimensions()
    {
        var work = WindowSizing.WorkAreaPxToDip(0, 0, 1366, 728, 1.25); // ~1092 x 582 DIP
        var r = WindowSizing.ComputeClampedBounds(work, new WindowSizing.WindowBounds(0, 0, 1180, 760));
        Assert.True(r.Width <= work.Width + 0.01);
        Assert.True(r.Height <= work.Height + 0.01);
        Assert.Equal(work.Width, r.Width, 0);
        Assert.Equal(work.Height, r.Height, 0);
    }

    [Fact] // A window already within the work area is left unchanged
    public void FittingWindow_Unchanged()
    {
        var work = WorkDip(1.00); // 1920 x 1032
        var desired = new WindowSizing.WindowBounds(100, 80, 1180, 760);
        var r = WindowSizing.ComputeClampedBounds(work, desired);
        Assert.Equal(desired.Left, r.Left, 0);
        Assert.Equal(desired.Top, r.Top, 0);
        Assert.Equal(desired.Width, r.Width, 0);
        Assert.Equal(desired.Height, r.Height, 0);
    }

    [Theory] // px→DIP conversion is exact at each scale
    [InlineData(1.00, 1920, 1032)]
    [InlineData(1.25, 1536, 825.6)]
    [InlineData(1.50, 1280, 688)]
    public void WorkAreaConversion_IsExact(double scale, double expectW, double expectH)
    {
        var dip = WindowSizing.WorkAreaPxToDip(0, 0, 1920, 1032, scale);
        Assert.Equal(expectW, dip.Width, 1);
        Assert.Equal(expectH, dip.Height, 1);
    }
}
