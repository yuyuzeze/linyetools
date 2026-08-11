using CommunityToolkit.Mvvm.ComponentModel;

namespace KikuCaption.App.Navigation;

/// <summary>
/// Default <see cref="INavigationService"/>. Page view models are supplied as factories (resolved
/// from DI once and cached) so navigating is a cheap reference swap that never re-creates a page —
/// a running meeting on the Home page keeps its state when the user visits another page and back
/// (UI-R1 §11 "page switching does not stop a running session").
/// </summary>
public sealed partial class NavigationService : ObservableObject, INavigationService
{
    private readonly Dictionary<PageKey, Func<object>> _factories = new();
    private readonly Dictionary<PageKey, object> _cache = new();

    [ObservableProperty]
    private object? _currentViewModel;

    [ObservableProperty]
    private PageKey _currentPage;

    /// <summary>Registers the factory that produces a page's view model on first navigation.</summary>
    public void Register(PageKey page, Func<object> factory) => _factories[page] = factory;

    public void Navigate(PageKey page)
    {
        if (CurrentViewModel is not null && CurrentPage == page && _cache.ContainsKey(page))
        {
            return; // already showing this page — do not re-resolve
        }

        if (!_cache.TryGetValue(page, out var vm))
        {
            if (!_factories.TryGetValue(page, out var factory))
            {
                throw new InvalidOperationException($"No page registered for {page}.");
            }

            vm = factory();
            _cache[page] = vm;
        }

        CurrentPage = page;
        CurrentViewModel = vm;
    }
}
