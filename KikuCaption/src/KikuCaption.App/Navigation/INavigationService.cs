using System.ComponentModel;

namespace KikuCaption.App.Navigation;

/// <summary>
/// In-window page navigation (UI-R1 §3). The shell binds a <c>ContentControl</c> to
/// <see cref="CurrentViewModel"/>; navigating swaps the hosted page view model without opening a new
/// window and without stopping a running meeting (page view models are singletons that keep their
/// state).
/// </summary>
public interface INavigationService : INotifyPropertyChanged
{
    /// <summary>The view model of the currently shown page.</summary>
    object? CurrentViewModel { get; }

    /// <summary>The key of the currently shown page.</summary>
    PageKey CurrentPage { get; }

    /// <summary>Switches the shown page. Idempotent for the already-current page.</summary>
    void Navigate(PageKey page);
}
