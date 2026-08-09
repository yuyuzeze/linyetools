using System.Windows;
using System.Windows.Controls;
using KikuCaption.App.ViewModels;

namespace KikuCaption.App.Behaviors;

/// <summary>
/// View-only glue between a virtualized <see cref="ListBox"/> and the
/// <see cref="MeetingTimelineViewModel"/> auto-scroll <i>decisions</i> (Milestone 3.1). This class
/// performs the pixel scrolling and reports the user's scroll position; it holds no business state.
///
/// <list type="bullet">
/// <item>When the timeline asks to follow the newest line (<c>ScrollToEndRequested</c>), it scrolls
/// the last item into view — after layout, so the freshly added container exists.</item>
/// <item>On a user-initiated scroll it tells the VM whether the view is pinned to the bottom, which
/// is how auto-scroll pauses (scroll up) and resumes (scroll back to bottom).</item>
/// </list>
///
/// Content-growth scroll events (new items) are ignored for the "at bottom" decision so a new final
/// can never be mistaken for the user scrolling away.
/// </summary>
public static class TimelineAutoScroll
{
    private const double BottomEpsilonPixels = 2.0;

    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.RegisterAttached(
        "ViewModel", typeof(MeetingTimelineViewModel), typeof(TimelineAutoScroll),
        new PropertyMetadata(null, OnViewModelChanged));

    // Holds the exact ScrollToEndRequested delegate so it can be detached if the VM is replaced.
    private static readonly DependencyProperty ScrollHandlerProperty = DependencyProperty.RegisterAttached(
        "ScrollHandler", typeof(EventHandler), typeof(TimelineAutoScroll), new PropertyMetadata(null));

    public static void SetViewModel(DependencyObject element, MeetingTimelineViewModel? value)
        => element.SetValue(ViewModelProperty, value);

    public static MeetingTimelineViewModel? GetViewModel(DependencyObject element)
        => (MeetingTimelineViewModel?)element.GetValue(ViewModelProperty);

    private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListBox listBox)
        {
            return;
        }

        if (e.OldValue is MeetingTimelineViewModel oldVm)
        {
            if (listBox.GetValue(ScrollHandlerProperty) is EventHandler oldHandler)
            {
                oldVm.ScrollToEndRequested -= oldHandler;
            }

            listBox.RemoveHandler(ScrollViewer.ScrollChangedEvent, (ScrollChangedEventHandler)OnScrollChanged);
        }

        if (e.NewValue is MeetingTimelineViewModel vm)
        {
            // ScrollChanged is a routed event bubbling from the inner ScrollViewer.
            listBox.AddHandler(ScrollViewer.ScrollChangedEvent, (ScrollChangedEventHandler)OnScrollChanged);
            EventHandler handler = (_, _) => ScrollToEnd(listBox);
            listBox.SetValue(ScrollHandlerProperty, handler);
            vm.ScrollToEndRequested += handler;
        }
    }

    private static void ScrollToEnd(ListBox listBox)
    {
        // Defer until the new item's container is generated so ScrollIntoView lands on it.
        listBox.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            int count = listBox.Items.Count;
            if (count > 0)
            {
                listBox.ScrollIntoView(listBox.Items[count - 1]);
            }
        });
    }

    private static void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (sender is not ListBox listBox || GetViewModel(listBox) is not { } vm)
        {
            return;
        }

        // Content grew (a new final was appended): not a user gesture — keep the current pin state.
        if (e.ExtentHeightChange != 0)
        {
            return;
        }

        double scrollableHeight = e.ExtentHeight - e.ViewportHeight;
        bool atBottom = scrollableHeight <= 0 || e.VerticalOffset >= scrollableHeight - BottomEpsilonPixels;
        vm.NotifyAtBottom(atBottom);
    }
}
