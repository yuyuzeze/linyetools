using System.IO;
using System.Windows;
using System.Windows.Controls;
using KikuCaption.App.ViewModels.Pages;

namespace KikuCaption.App.Views.Pages;

/// <summary>
/// Home page view. Code-behind is limited to unavoidable view behaviours: WPF file dialogs (which
/// are view concerns) and the PasswordBox → DPAPI bridge (the key is never bound, echoed or logged).
/// All business logic lives in the view models.
/// </summary>
public partial class HomePage : UserControl
{
    public HomePage() => InitializeComponent();

    private HomePageViewModel? ViewModel => DataContext as HomePageViewModel;

    // The view only picks the output file (a WPF dialog, not audio logic) and delegates the actual
    // capture to the view model / recorder.
    private async void StartCapture_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        var audio = ViewModel.Audio;
        var suggested = audio.SuggestDefaultOutputPath();
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
            await audio.StartAsync(dialog.FileName);
        }
    }

    // The API key is read from the PasswordBox and handed straight to the DPAPI secret store; it is
    // never bound, echoed, or logged (PROJECT.md 5.6, M6 §8).
    private void SaveApiKey_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        ViewModel.Translation.SaveApiKey(ApiKeyBox.Password);
        ApiKeyBox.Clear();
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
