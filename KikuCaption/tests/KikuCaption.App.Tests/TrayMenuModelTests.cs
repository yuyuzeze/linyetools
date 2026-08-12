using System.Linq;
using KikuCaption.App.Localization;
using KikuCaption.App.Tray;
using KikuCaption.Core.Enums;
using Xunit;

namespace KikuCaption.App.Tests;

/// <summary>UI-R5B: pure tray menu/tooltip derivation from session state + overlay + language.</summary>
public class TrayMenuModelTests
{
    private static LocalizationService Loc(string culture = LocalizedStrings.ZhCN)
    {
        var loc = new LocalizationService();
        loc.SetLanguage(culture);
        return loc;
    }

    private static TrayMenuItem Item(System.Collections.Generic.IReadOnlyList<TrayMenuItem> items, TrayCommand c)
        => items.First(i => i.Command == c);

    [Fact] // scenario 10: idle → Start enabled, Stop disabled
    public void Idle_StartEnabled_StopDisabled()
    {
        var items = TrayMenuModel.Build(SessionState.Idle, overlayVisible: false, Loc());
        Assert.True(Item(items, TrayCommand.StartSession).Enabled);
        Assert.False(Item(items, TrayCommand.StopSession).Enabled);
    }

    [Theory] // scenario 11: running/starting → Stop enabled, Start disabled
    [InlineData(SessionState.Running)]
    [InlineData(SessionState.Starting)]
    [InlineData(SessionState.Preflight)]
    public void Running_StopEnabled_StartDisabled(SessionState state)
    {
        var items = TrayMenuModel.Build(state, false, Loc());
        Assert.False(Item(items, TrayCommand.StartSession).Enabled);
        Assert.True(Item(items, TrayCommand.StopSession).Enabled);
    }

    [Fact] // scenario 12: stopping → both disabled (prevents a repeat stop / early start)
    public void Stopping_BothDisabled()
    {
        var items = TrayMenuModel.Build(SessionState.Stopping, false, Loc());
        Assert.False(Item(items, TrayCommand.StartSession).Enabled);
        Assert.False(Item(items, TrayCommand.StopSession).Enabled);
    }

    [Fact] // Faulted follows the state machine: a new session may start (CanStart)
    public void Faulted_AllowsStart()
        => Assert.True(Item(TrayMenuModel.Build(SessionState.Faulted, false, Loc()), TrayCommand.StartSession).Enabled);

    [Fact] // scenario 16: the overlay entry text follows the real overlay visibility
    public void OverlayToggle_TextFollowsVisibility()
    {
        var hidden = Item(TrayMenuModel.Build(SessionState.Idle, overlayVisible: false, Loc()), TrayCommand.ToggleOverlay);
        var shown = Item(TrayMenuModel.Build(SessionState.Idle, overlayVisible: true, Loc()), TrayCommand.ToggleOverlay);
        Assert.Equal("显示字幕浮窗", hidden.Text);
        Assert.Equal("隐藏字幕浮窗", shown.Text);
    }

    [Fact] // scenario 19: the tooltip reflects the session status
    public void Tooltip_ReflectsStatus()
    {
        Assert.Contains("空闲", TrayMenuModel.Tooltip(SessionState.Idle, null, Loc()));
        Assert.Contains("正在启动", TrayMenuModel.Tooltip(SessionState.Starting, null, Loc()));
        Assert.Contains("正在停止", TrayMenuModel.Tooltip(SessionState.Stopping, null, Loc()));
        Assert.Contains("错误", TrayMenuModel.Tooltip(SessionState.Faulted, null, Loc()));
    }

    [Fact] // scenario 20: while recording the tooltip shows the running time
    public void Tooltip_Recording_ShowsElapsed()
    {
        var text = TrayMenuModel.Tooltip(SessionState.Running, TimeSpan.FromSeconds(2120), Loc()); // 35:20
        Assert.Contains("录制中", text);
        Assert.Contains("35:20", text);
    }

    [Fact] // the tooltip never exceeds the NotifyIcon length limit
    public void Tooltip_IsClamped()
        => Assert.True(TrayMenuModel.Tooltip(SessionState.Running, TimeSpan.FromHours(9999), Loc()).Length <= 63);

    [Fact] // scenario 21: the menu localizes (zh vs en vs ja)
    public void Menu_Localizes()
    {
        Assert.Equal("开始会话", Item(TrayMenuModel.Build(SessionState.Idle, false, Loc(LocalizedStrings.ZhCN)), TrayCommand.StartSession).Text);
        Assert.Equal("Start session", Item(TrayMenuModel.Build(SessionState.Idle, false, Loc(LocalizedStrings.EnUS)), TrayCommand.StartSession).Text);
        Assert.Equal("会話を開始", Item(TrayMenuModel.Build(SessionState.Idle, false, Loc(LocalizedStrings.JaJP)), TrayCommand.StartSession).Text);
    }

    [Fact] // scenario 31: tray strings carry only status words — never caption text, paths, or secrets
    public void Tooltip_ContainsNoSensitiveData()
    {
        var text = TrayMenuModel.Tooltip(SessionState.Running, TimeSpan.FromSeconds(5), Loc());
        Assert.DoesNotContain("C:\\", text);
        Assert.DoesNotContain("apikey", text, StringComparison.OrdinalIgnoreCase);
    }
}
