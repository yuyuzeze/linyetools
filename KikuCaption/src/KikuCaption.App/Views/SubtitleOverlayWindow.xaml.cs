using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using KikuCaption.App.ViewModels;

namespace KikuCaption.App.Views;

/// <summary>
/// Always-on-top, draggable, click-through-capable subtitle overlay. Code-behind only carries
/// window/OS behavior (extended styles, drag, positioning); all caption state is in the view model.
/// </summary>
public partial class SubtitleOverlayWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TRANSPARENT = 0x20;
    private const long WS_EX_LAYERED = 0x80000;
    private const long WS_EX_NOACTIVATE = 0x08000000;
    private const long WS_EX_TOOLWINDOW = 0x80;

    private readonly SubtitleOverlayViewModel _viewModel;
    private IntPtr _hwnd;

    public SubtitleOverlayWindow(SubtitleOverlayViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        SizeChanged += (_, _) => Reposition();
        MouseLeftButtonDown += OnMouseLeftButtonDown;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;

        // Never steal focus (e.g. from Teams) and stay out of Alt-Tab.
        long exStyle = (long)GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
        exStyle |= WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, (IntPtr)exStyle);

        ApplyClickThrough(_viewModel.ClickThrough);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SubtitleOverlayViewModel.IsVisible):
                if (_viewModel.IsVisible)
                {
                    Show();
                    Reposition();
                }
                else
                {
                    Hide();
                }

                break;

            case nameof(SubtitleOverlayViewModel.ClickThrough):
                ApplyClickThrough(_viewModel.ClickThrough);
                break;
        }
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Dragging only makes sense when the window is interactive (not click-through).
        if (!_viewModel.ClickThrough && e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { /* ignore */ }
        }
    }

    private void ApplyClickThrough(bool enabled)
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        long exStyle = (long)GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
        exStyle = enabled ? (exStyle | WS_EX_TRANSPARENT) : (exStyle & ~WS_EX_TRANSPARENT);
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, (IntPtr)exStyle);
    }

    private void Reposition()
    {
        if (!IsVisible)
        {
            return;
        }

        var workArea = SystemParameters.WorkArea;
        Left = workArea.Left + Math.Max(0, (workArea.Width - ActualWidth) / 2);
        Top = workArea.Top + Math.Max(0, workArea.Height - ActualHeight - 64);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
}
