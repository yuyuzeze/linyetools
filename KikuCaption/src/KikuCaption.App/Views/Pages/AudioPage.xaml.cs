using System.IO;
using System.Windows;
using System.Windows.Controls;
using KikuCaption.App.ViewModels.Pages;

namespace KikuCaption.App.Views.Pages;

/// <summary>
/// Audio page view. Code-behind is limited to WPF file dialogs (a view concern) that delegate the
/// actual capture/recognition to the view models.
/// </summary>
public partial class AudioPage : UserControl
{
    public AudioPage() => InitializeComponent();

    private AudioPageViewModel? ViewModel => DataContext as AudioPageViewModel;

    private async void StartCapture_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        var capture = ViewModel.Capture;
        var suggested = capture.SuggestDefaultOutputPath();
        var initialDirectory = Path.GetDirectoryName(suggested);
        if (!string.IsNullOrEmpty(initialDirectory))
        {
            Directory.CreateDirectory(initialDirectory);
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "保存系统音频 WAV",
            Filter = "WAV 音频 (*.wav)|*.wav",
            FileName = Path.GetFileName(suggested),
            InitialDirectory = initialDirectory,
            AddExtension = true,
            DefaultExt = ".wav",
            OverwritePrompt = true
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            await capture.StartAsync(dialog.FileName);
        }
    }

    private async void RecognizeWav_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要识别的 WAV 文件",
            Filter = "WAV 音频 (*.wav)|*.wav",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            await ViewModel.Speech.RecognizeWavAsync(dialog.FileName);
        }
    }
}
