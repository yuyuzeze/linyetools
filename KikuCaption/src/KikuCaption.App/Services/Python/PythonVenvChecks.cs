namespace KikuCaption.App.Services.Python;

/// <summary>
/// The single import/runtime verification script shared by the installer and crash-recovery (R7C.1) so
/// the two never diverge on what "a healthy venv" means: faster_whisper / ctranslate2 / av / numpy all
/// import AND CTranslate2 reports CPU int8 support. Emits one JSON line.
/// </summary>
internal static class PythonVenvChecks
{
    public const string ImportsScript =
        "import json,sys,faster_whisper,ctranslate2,av,numpy;" +
        "print(json.dumps({'py':'%d.%d.%d'%sys.version_info[:3],'fw':faster_whisper.__version__,'ct2':ctranslate2.__version__,'int8':'int8' in ctranslate2.get_supported_compute_types('cpu')}))";
}
