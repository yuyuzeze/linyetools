using System.Windows;
using KikuCaption.App.Localization;
using KikuCaption.App.ViewModels.Pages;

namespace KikuCaption.App.Services;

/// <summary>
/// WPF implementation of <see cref="IDictionaryPrompts"/> using localized <c>MessageBox</c> dialogs.
/// The dictionary view model stays UI-free; this is the only place the page touches a dialog.
/// </summary>
public sealed class DictionaryPrompts : IDictionaryPrompts
{
    private readonly LocalizationService _loc;

    public DictionaryPrompts(LocalizationService loc) => _loc = loc;

    public UnsavedChangesChoice ConfirmUnsavedChanges()
    {
        var result = MessageBox.Show(
            _loc["Dict.UnsavedMessage"],
            _loc["Dict.UnsavedTitle"],
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        return result switch
        {
            MessageBoxResult.Yes => UnsavedChangesChoice.Save,
            MessageBoxResult.No => UnsavedChangesChoice.Discard,
            _ => UnsavedChangesChoice.Cancel
        };
    }

    public bool ConfirmDelete(bool isActive)
    {
        var message = isActive ? _loc["Dict.DeleteActiveMessage"] : _loc["Dict.DeleteConfirmMessage"];
        var result = MessageBox.Show(
            message,
            _loc["Dict.DeleteConfirmTitle"],
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        return result == MessageBoxResult.OK;
    }
}
