using KikuCaption.Core.Enums;
using KikuCaption.Core.Interfaces;
using KikuCaption.Core.Models;
using Microsoft.Extensions.Logging;

namespace KikuCaption.Speech.SpeechCpp;

/// <summary>
/// Default <see cref="ISpeechRecognizerFactory"/>: returns the speech.cpp recognizer when the
/// session asks for it AND the backend is available, otherwise the Whisper recognizer. The
/// fallback is silent-but-logged so a missing speech.cpp install degrades to Whisper rather than
/// faulting the session (the UI surfaces the choice separately).
/// </summary>
public sealed class SpeechRecognizerFactory : ISpeechRecognizerFactory
{
    private readonly Func<ISpeechRecognizer> _whisper;
    private readonly Func<ISpeechRecognizer> _speechCpp;
    private readonly ISpeechCppEnvironment _environment;
    private readonly ILogger<SpeechRecognizerFactory> _logger;

    public SpeechRecognizerFactory(
        Func<ISpeechRecognizer> whisper,
        Func<ISpeechRecognizer> speechCpp,
        ISpeechCppEnvironment environment,
        ILogger<SpeechRecognizerFactory> logger)
    {
        _whisper = whisper;
        _speechCpp = speechCpp;
        _environment = environment;
        _logger = logger;
    }

    public ISpeechRecognizer Create(SpeechOptions options)
    {
        if (options.Engine == SpeechEngine.SpeechCpp)
        {
            if (_environment.IsAvailable)
            {
                return _speechCpp();
            }

            _logger.LogWarning("SpeechCpp 引擎不可用（未安装 speech.exe 或模型），回退到 Whisper。");
        }

        return _whisper();
    }
}
