using System.Windows;
using KikuCaption.App.Localization;
using KikuCaption.App.ViewModels;

namespace KikuCaption.App.Views;

/// <summary>
/// The "generate meeting summary" dialog (UI-R5C). All logic is in
/// <see cref="MeetingSummaryDialogViewModel"/>; the code-behind only confirms cancellation if the user
/// tries to close while a generation is still running (the task is cancelled, temp files cleaned up).
/// </summary>
public partial class MeetingSummaryDialog : Window
{
    private readonly MeetingSummaryDialogViewModel _vm;

    public MeetingSummaryDialog(MeetingSummaryDialogViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Loaded += async (_, _) => await _vm.InitializeAsync(); // load count + validated duration
        Closing += OnClosing;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_vm.IsBusy)
        {
            return;
        }

        var loc = LocalizationService.Instance;
        var choice = MessageBox.Show(loc["Summary.CancelBtn"], loc["Summary.Title"],
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (choice == MessageBoxResult.Yes)
        {
            _vm.CancelCommand.Execute(null); // cancel the in-flight generation, then allow the close
        }
        else
        {
            e.Cancel = true; // keep generating
        }
    }
}
