using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using KikuCaption.App.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace KikuCaption.App.Tests;

/// <summary>
/// Opt-in (STA, gated by <c>KIKU_UI=1</c>) proof that the timeline ListBox really virtualizes:
/// with 5000 items it realizes only a viewport's worth of containers, so scrolling stays smooth
/// and no subtitle data is dropped. Off by default because it needs an STA thread and offscreen
/// WPF layout; run explicitly and read the realized-container count from the test output.
/// </summary>
public sealed class TimelineVirtualizationTests
{
    private readonly ITestOutputHelper _output;

    public TimelineVirtualizationTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void ListBox_Virtualizes_5000_Items()
    {
        if (Environment.GetEnvironmentVariable("KIKU_UI") != "1")
        {
            _output.WriteLine("Gated UI test skipped (set KIKU_UI=1 to run).");
            return;
        }

        int realized = -1;
        int total = 5000;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                var items = Enumerable.Range(1, total)
                    .Select(i => new CaptionEntryViewModel(Guid.NewGuid(), i, DateTimeOffset.Now, $"第{i}文の内容"))
                    .ToList();

                var listBox = new ListBox
                {
                    ItemsSource = items,
                    Width = 400,
                    Height = 600
                };
                VirtualizingPanel.SetIsVirtualizing(listBox, true);
                VirtualizingPanel.SetVirtualizationMode(listBox, VirtualizationMode.Recycling);
                VirtualizingPanel.SetScrollUnit(listBox, ScrollUnit.Pixel);
                ScrollViewer.SetCanContentScroll(listBox, true);

                // Host in a real (invisible) presentation source so the virtualizer establishes a
                // viewport and generates containers during synchronous layout.
                var hwnd = new HwndSource(new HwndSourceParameters("kiku-vtest")
                {
                    Width = 400,
                    Height = 600
                })
                {
                    RootVisual = listBox
                };
                try
                {
                    listBox.UpdateLayout();

                    realized = 0;
                    for (int i = 0; i < items.Count; i++)
                    {
                        if (listBox.ItemContainerGenerator.ContainerFromIndex(i) is not null)
                        {
                            realized++;
                        }
                    }
                }
                finally
                {
                    hwnd.Dispose();
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
        _output.WriteLine($"realized containers = {realized} of {total}");
        Assert.True(realized > 0, "no containers realized — layout did not run");
        Assert.True(realized < 300, $"expected virtualization (few realized), got {realized}/{total}");
    }
}
