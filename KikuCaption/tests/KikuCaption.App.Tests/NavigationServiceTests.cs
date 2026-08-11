using KikuCaption.App.Navigation;
using Xunit;

namespace KikuCaption.App.Tests;

/// <summary>UI-R1 §3 in-window navigation: correct routing, no duplicate view models, idempotence.</summary>
public class NavigationServiceTests
{
    private sealed class HomeVm { }
    private sealed class EnvVm { }

    private sealed class Harness
    {
        public NavigationService Nav { get; } = new();
        public int HomeBuilds;
        public int EnvBuilds;

        public Harness()
        {
            Nav.Register(PageKey.Home, () => { HomeBuilds++; return new HomeVm(); });
            Nav.Register(PageKey.Environment, () => { EnvBuilds++; return new EnvVm(); });
        }
    }

    [Fact]
    public void Navigate_RoutesToCorrectPage()
    {
        var h = new Harness();

        h.Nav.Navigate(PageKey.Home);
        Assert.Equal(PageKey.Home, h.Nav.CurrentPage);
        Assert.IsType<HomeVm>(h.Nav.CurrentViewModel);

        h.Nav.Navigate(PageKey.Environment);
        Assert.Equal(PageKey.Environment, h.Nav.CurrentPage);
        Assert.IsType<EnvVm>(h.Nav.CurrentViewModel);
    }

    [Fact]
    public void Navigate_DoesNotRecreateViewModels()
    {
        var h = new Harness();

        h.Nav.Navigate(PageKey.Home);
        var firstHome = h.Nav.CurrentViewModel;
        h.Nav.Navigate(PageKey.Environment);
        h.Nav.Navigate(PageKey.Home); // back to home

        Assert.Same(firstHome, h.Nav.CurrentViewModel); // same cached instance
        Assert.Equal(1, h.HomeBuilds);                  // factory ran exactly once
        Assert.Equal(1, h.EnvBuilds);
    }

    [Fact]
    public void Navigate_SamePageTwice_IsIdempotent()
    {
        var h = new Harness();

        h.Nav.Navigate(PageKey.Home);
        h.Nav.Navigate(PageKey.Home);

        Assert.Equal(1, h.HomeBuilds);
    }

    [Fact]
    public void Navigate_RaisesPropertyChanged()
    {
        var h = new Harness();
        var changed = new List<string?>();
        h.Nav.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        // Navigate to Environment: CurrentPage changes off its Home default, so both fire.
        h.Nav.Navigate(PageKey.Environment);

        Assert.Contains(nameof(INavigationService.CurrentViewModel), changed);
        Assert.Contains(nameof(INavigationService.CurrentPage), changed);
    }

    [Fact]
    public void Navigate_UnregisteredPage_Throws()
    {
        var nav = new NavigationService();
        Assert.Throws<InvalidOperationException>(() => nav.Navigate(PageKey.Settings));
    }
}
