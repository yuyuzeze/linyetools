using System.Windows;
using System.Windows.Input;
using KikuCaption.App.Playback;

namespace KikuCaption.App.Views;

public partial class CaptionSearchDialog : Window
{
    private readonly CaptionSearchViewModel _viewModel;

    public CaptionSearchDialog(IEnumerable<CaptionSearchSource> sources)
    {
        InitializeComponent();
        _viewModel = new CaptionSearchViewModel(sources);
        DataContext = _viewModel;
        Loaded += (_, _) => QueryBox.Focus();
    }

    public CaptionSearchResult? SelectedResult => _viewModel.SelectedResult;

    private void Jump_Click(object sender, RoutedEventArgs e) => AcceptSelection();

    private void ResultList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => AcceptSelection();

    private void AcceptSelection()
    {
        if (_viewModel.SelectedResult is null) return;
        DialogResult = true;
    }
}
