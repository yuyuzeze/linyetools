using System.IO;
using KikuCaption.ComponentManagement.Installing;
using KikuCaption.ComponentManagement.Manifest;
using KikuCaption.Core.Models;

namespace KikuCaption.App.Services;

/// <summary>
/// R7B.1 model verifier run on the extracted staging copy before install. It ALWAYS does a structural
/// check (model.bin present + large enough). Then, if a healthy Python env exists, it performs a REAL
/// faster-whisper/CTranslate2 load of the extracted directory:
/// <list type="bullet">
/// <item>load succeeds → <see cref="ModelVerificationLevel.RuntimeVerified"/>;</item>
/// <item>no healthy venv → <see cref="ModelVerificationLevel.StructureVerified"/> (the UI then says the
/// model is downloaded but not yet runtime-verified);</item>
/// <item>load fails → throws (Failed), so a corrupt model never replaces an existing good one.</item>
/// </list>
/// It never creates or installs a venv (that is R7C).
/// </summary>
public sealed class WhisperModelVerifier : IComponentVerifier
{
    private readonly IModelRuntimeLoader _runtimeLoader;
    private readonly long _minModelBinBytes;

    public WhisperModelVerifier(IModelRuntimeLoader runtimeLoader, long minModelBinBytes = 10_000_000L)
    {
        _runtimeLoader = runtimeLoader;
        _minModelBinBytes = minModelBinBytes;
    }

    public async Task<ModelVerificationLevel> VerifyAsync(RemoteComponent component, string extractedDirectory, CancellationToken cancellationToken)
    {
        if (component.Type != RemoteComponentType.WhisperModel)
        {
            return ModelVerificationLevel.StructureVerified;
        }

        var modelBin = Path.Combine(extractedDirectory, "model.bin");
        if (!File.Exists(modelBin) || new FileInfo(modelBin).Length < _minModelBinBytes)
        {
            throw new ComponentInstallException("verify",
                "The downloaded model.bin is missing or too small to be a valid model.");
        }

        var result = await _runtimeLoader.TryLoadAsync(extractedDirectory, cancellationToken).ConfigureAwait(false);
        return result switch
        {
            ModelRuntimeLoadResult.Loaded => ModelVerificationLevel.RuntimeVerified,
            ModelRuntimeLoadResult.VenvUnavailable => ModelVerificationLevel.StructureVerified,
            _ => throw new ComponentInstallException("runtime-verify",
                "The downloaded model failed to load with faster-whisper."),
        };
    }
}
