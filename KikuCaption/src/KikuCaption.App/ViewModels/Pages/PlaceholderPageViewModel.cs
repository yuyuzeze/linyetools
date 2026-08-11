using CommunityToolkit.Mvvm.ComponentModel;

namespace KikuCaption.App.ViewModels.Pages;

/// <summary>
/// A minimal placeholder for information-architecture entries whose dedicated pages are built in a
/// later UI phase (Audio → UI-R2, Settings → UI-R3, Dictionary → UI-R4). It exists only so the top
/// navigation is complete and testable in UI-R1; it implements no feature behaviour. The related
/// functionality remains available on the Home page until its page is built.
/// </summary>
public sealed partial class PlaceholderPageViewModel : ObservableObject
{
    public PlaceholderPageViewModel(string title, string description)
    {
        Title = title;
        Description = description;
    }

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private string _description;
}
