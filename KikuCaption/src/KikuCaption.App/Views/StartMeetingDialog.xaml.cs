using System.Windows;
using KikuCaption.App.ViewModels;

namespace KikuCaption.App.Views;

/// <summary>
/// Compact "start meeting" dialog (UI-R2). It edits an independent draft; the caller applies the
/// draft to the live meeting state only when this returns true (via <see cref="MeetingStartCoordinator"/>).
/// Cancel / Esc / window-close all resolve to a non-true result, so the main view model is untouched.
/// Code-behind is limited to setting the confirm result.
/// </summary>
public partial class StartMeetingDialog : Window
{
    public StartMeetingDialog(StartMeetingDialogViewModel draft)
    {
        InitializeComponent();
        DataContext = draft;
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is StartMeetingDialogViewModel draft && draft.CanStart)
        {
            DialogResult = true;
        }
    }
}
