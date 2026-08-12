using System.Windows;
using WinForms = System.Windows.Forms;

namespace KikuCaption.App.Tray;

/// <summary>
/// The production <see cref="ITrayIconAdapter"/> — a thin wrapper over
/// <c>System.Windows.Forms.NotifyIcon</c> (UI-R5B). It owns exactly one notification-area icon for
/// the app lifetime, rebuilds a <c>ContextMenuStrip</c> from the model, and forwards menu clicks /
/// double-clicks as shell-agnostic events. Created on and used from the WPF UI thread, so NotifyIcon
/// callbacks arrive there. Dispose hides + disposes the icon so no ghost remains in the tray.
/// </summary>
public sealed class WinFormsTrayIconAdapter : ITrayIconAdapter
{
    private readonly WinForms.NotifyIcon _icon;
    private WinForms.ContextMenuStrip? _menu;
    private bool _disposed;

    public event Action<TrayCommand>? CommandInvoked;
    public event Action? DoubleClicked;

    public WinFormsTrayIconAdapter()
    {
        _icon = new WinForms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "KikuCaption",
            Visible = false
        };
        _icon.DoubleClick += (_, _) => DoubleClicked?.Invoke();
    }

    public bool Visible
    {
        get => _icon.Visible;
        set => _icon.Visible = value;
    }

    public void SetTooltip(string text)
    {
        // NotifyIcon.Text throws on an over-long value; the model already clamps, but guard anyway.
        _icon.Text = text.Length <= 63 ? text : text[..63];
    }

    public void SetMenu(IReadOnlyList<TrayMenuItem> items)
    {
        var strip = new WinForms.ContextMenuStrip();
        foreach (var item in items)
        {
            if (!item.Visible)
            {
                continue;
            }

            var command = item.Command;
            var entry = new WinForms.ToolStripMenuItem(item.Text) { Enabled = item.Enabled };
            entry.Click += (_, _) => CommandInvoked?.Invoke(command);
            strip.Items.Add(entry);
        }

        var old = _menu;
        _menu = strip;
        _icon.ContextMenuStrip = strip;
        old?.Dispose();
    }

    public void ShowBalloon(string title, string text)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = text;
        _icon.ShowBalloonTip(3000);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        _icon.Visible = false; // remove from the tray immediately (no ghost icon)
        _icon.Dispose();
        _menu?.Dispose();
    }

    private static System.Drawing.Icon LoadIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Resources/app.ico", UriKind.Absolute);
            var stream = Application.GetResourceStream(uri)?.Stream;
            if (stream is not null)
            {
                using (stream)
                {
                    return new System.Drawing.Icon(stream);
                }
            }
        }
        catch { /* fall through to the system default below */ }

        return System.Drawing.SystemIcons.Application;
    }
}
