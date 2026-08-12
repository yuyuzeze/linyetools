using System.Windows.Controls;

namespace KikuCaption.App.Views.Pages;

/// <summary>
/// Dictionary page view (UI-R4B). All behaviour lives in <see cref="ViewModels.Pages.DictionaryPageViewModel"/>;
/// this control only hosts the template. Unsaved-changes and delete confirmations are surfaced through
/// the injected <c>IDictionaryPrompts</c>, so the code-behind stays empty.
/// </summary>
public partial class DictionaryPage : UserControl
{
    public DictionaryPage() => InitializeComponent();
}
