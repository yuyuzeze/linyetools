using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using KikuCaption.Core.Interfaces;
using KikuCaption.Core.Models;
using Microsoft.Extensions.Logging;

namespace KikuCaption.Infrastructure.Configuration;

/// <summary>
/// File-backed <see cref="ISpeechDictionaryStore"/> (UI-R4B). Persists dictionaries and the active
/// selection per language to <c>%LOCALAPPDATA%/KikuCaption/dictionaries.json</c> — a user-writable
/// location, never the install directory, never SQLite, never the translation config.
///
/// Guarantees: atomic writes (temp + replace); a corrupt file is backed up as
/// <c>dictionaries.corrupt-*.bak</c> and safe defaults are restored (never a silent overwrite);
/// all state access is serialized by a lock; every returned profile is a defensive copy so callers
/// cannot mutate stored state; only ProfileId / language / hotword-count are ever logged — never the
/// prompt or hotword text. Two built-in dictionaries are seeded (idempotently) from the appsettings
/// <c>Speech:Contexts</c> and cannot be edited or deleted.
/// </summary>
public sealed class SpeechDictionaryStore : ISpeechDictionaryStore
{
    private const int SchemaVersion = 1;

    // Canonical (fallback) built-in names; the UI shows a localized name for built-ins instead.
    private static readonly IReadOnlyDictionary<string, string> BuiltInNames = new Dictionary<string, string>
    {
        ["ja"] = "默认日语技术词典",
        ["zh"] = "默认中文技术词典"
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _gate = new();
    private readonly string _path;
    private readonly IReadOnlyDictionary<string, SpeechContext> _seeds;
    private readonly ILogger<SpeechDictionaryStore> _logger;
    private readonly TimeProvider _clock;

    private FileModel _state = new();

    public SpeechDictionaryStore(
        string directory,
        IReadOnlyDictionary<string, SpeechContext> seeds,
        ILogger<SpeechDictionaryStore> logger,
        TimeProvider? clock = null)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "dictionaries.json");
        _seeds = seeds ?? new Dictionary<string, SpeechContext>();
        _logger = logger;
        _clock = clock ?? TimeProvider.System;

        lock (_gate)
        {
            _state = LoadOrRecover();
            var changed = EnsureBuiltInsAndActive(_state);
            if (changed)
            {
                Persist(_state);
            }
        }

        _logger.LogInformation(
            "Speech dictionary store ready: {ProfileCount} profiles, active ja={ActiveJa} zh={ActiveZh}.",
            _state.Profiles.Count, _state.ActiveByLanguage.GetValueOrDefault("ja"), _state.ActiveByLanguage.GetValueOrDefault("zh"));
    }

    /// <summary>Default location: <c>%LOCALAPPDATA%/KikuCaption</c> (shared with other user prefs).</summary>
    public static string DefaultDirectory
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KikuCaption");

    public string FilePath => _path;

    public IReadOnlyList<SpeechDictionaryProfile> GetProfiles(string? languageCode = null)
    {
        lock (_gate)
        {
            return _state.Profiles
                .Where(p => languageCode is null || string.Equals(p.LanguageCode, languageCode, StringComparison.Ordinal))
                .Select(ToDomain)
                .ToList();
        }
    }

    public SpeechDictionaryProfile? GetById(Guid id)
    {
        lock (_gate)
        {
            var dto = _state.Profiles.FirstOrDefault(p => p.Id == id);
            return dto is null ? null : ToDomain(dto);
        }
    }

    public Guid GetActiveId(string languageCode)
    {
        lock (_gate)
        {
            return ResolveActiveDto(languageCode).Id;
        }
    }

    public SpeechDictionaryProfile GetActiveProfile(string languageCode)
    {
        lock (_gate)
        {
            return ToDomain(ResolveActiveDto(languageCode));
        }
    }

    public SpeechDictionaryProfile Upsert(SpeechDictionaryProfile profile)
    {
        var normalized = profile.Normalized(); // shared front/back validation (name/prompt/hotwords)

        lock (_gate)
        {
            var existing = _state.Profiles.FirstOrDefault(p => p.Id == normalized.Id);
            if (existing is { IsBuiltIn: true })
            {
                throw new InvalidOperationException("内置词典不可修改，请先复制为用户词典。");
            }

            // Per-language name uniqueness (case/trim-insensitive), excluding the profile itself.
            var clash = _state.Profiles.Any(p =>
                p.Id != normalized.Id &&
                string.Equals(p.LanguageCode, normalized.LanguageCode, StringComparison.Ordinal) &&
                string.Equals(p.Name.Trim(), normalized.Name, StringComparison.OrdinalIgnoreCase));
            if (clash)
            {
                throw new ArgumentException($"同一识别语言下已存在同名词典「{normalized.Name}」。", nameof(profile));
            }

            var now = _clock.GetUtcNow();
            var id = normalized.Id == Guid.Empty ? Guid.NewGuid() : normalized.Id;

            var dto = new ProfileDto
            {
                Id = id,
                Name = normalized.Name,
                LanguageCode = normalized.LanguageCode,
                InitialPrompt = normalized.InitialPrompt,
                Hotwords = normalized.Hotwords.ToList(),
                IsBuiltIn = false,
                CreatedAt = existing?.CreatedAt ?? now,
                UpdatedAt = now
            };

            if (existing is null)
            {
                _state.Profiles.Add(dto);
            }
            else
            {
                _state.Profiles[_state.Profiles.IndexOf(existing)] = dto;
            }

            Persist(_state);
            LogProfile(existing is null ? "created" : "updated", dto);
            return ToDomain(dto);
        }
    }

    public void SetActive(string languageCode, Guid id)
    {
        lock (_gate)
        {
            var dto = _state.Profiles.FirstOrDefault(p => p.Id == id)
                ?? throw new ArgumentException($"词典 {id} 不存在。", nameof(id));
            if (!string.Equals(dto.LanguageCode, languageCode, StringComparison.Ordinal))
            {
                throw new ArgumentException($"词典 {id} 的语言（{dto.LanguageCode}）与 {languageCode} 不一致。", nameof(languageCode));
            }

            _state.ActiveByLanguage[languageCode] = id;
            Persist(_state);
            _logger.LogInformation("Active speech dictionary set: {Language} -> {ProfileId}.", languageCode, id);
        }
    }

    public void Delete(Guid id)
    {
        lock (_gate)
        {
            var dto = _state.Profiles.FirstOrDefault(p => p.Id == id)
                ?? throw new ArgumentException($"词典 {id} 不存在。", nameof(id));
            if (dto.IsBuiltIn)
            {
                throw new InvalidOperationException("内置词典不可删除。");
            }

            _state.Profiles.Remove(dto);

            // If this was the active dictionary for its language, fall back to the built-in — the
            // removal and the active remap are persisted together in a single atomic write.
            if (_state.ActiveByLanguage.TryGetValue(dto.LanguageCode, out var activeId) && activeId == id)
            {
                _state.ActiveByLanguage[dto.LanguageCode] =
                    SpeechDictionaryProfile.BuiltInIdFor(dto.LanguageCode) ?? Guid.Empty;
            }

            Persist(_state);
            _logger.LogInformation("Deleted speech dictionary: {ProfileId} ({Language}).", id, dto.LanguageCode);
        }
    }

    public void RestoreBuiltInDefaults()
    {
        lock (_gate)
        {
            foreach (var lang in SpeechDictionaryProfile.SupportedLanguages)
            {
                var builtInId = SpeechDictionaryProfile.BuiltInIdFor(lang)!.Value;
                var seeded = BuildBuiltIn(lang, _clock.GetUtcNow());
                var existing = _state.Profiles.FirstOrDefault(p => p.Id == builtInId);
                if (existing is null)
                {
                    _state.Profiles.Add(seeded);
                }
                else
                {
                    seeded.CreatedAt = existing.CreatedAt;
                    _state.Profiles[_state.Profiles.IndexOf(existing)] = seeded;
                }
            }

            Persist(_state);
            _logger.LogInformation("Restored built-in speech dictionaries to seeded defaults.");
        }
    }

    // ---- internals -------------------------------------------------------

    private ProfileDto ResolveActiveDto(string languageCode)
    {
        if (_state.ActiveByLanguage.TryGetValue(languageCode, out var activeId))
        {
            var active = _state.Profiles.FirstOrDefault(p => p.Id == activeId);
            if (active is not null)
            {
                return active;
            }
        }

        // No/dangling active selection → the language's built-in (create an ephemeral one if needed).
        var builtInId = SpeechDictionaryProfile.BuiltInIdFor(languageCode);
        if (builtInId is not null)
        {
            var builtIn = _state.Profiles.FirstOrDefault(p => p.Id == builtInId.Value);
            if (builtIn is not null)
            {
                return builtIn;
            }
        }

        return BuildBuiltIn(SpeechDictionaryProfile.IsSupportedLanguage(languageCode) ? languageCode : "ja", _clock.GetUtcNow());
    }

    private FileModel LoadOrRecover()
    {
        if (!File.Exists(_path))
        {
            return new FileModel();
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(_path), JsonOptions);
            if (loaded is null)
            {
                return BackupAndReset();
            }

            loaded.Profiles ??= new List<ProfileDto>();
            loaded.ActiveByLanguage ??= new Dictionary<string, Guid>(StringComparer.Ordinal);
            // Drop any structurally invalid rows so one bad entry can't crash the app at startup.
            loaded.Profiles = loaded.Profiles
                .Where(p => p is not null && p.Id != Guid.Empty && !string.IsNullOrWhiteSpace(p.Name) && !string.IsNullOrWhiteSpace(p.LanguageCode))
                .ToList();
            foreach (var p in loaded.Profiles)
            {
                p.Hotwords ??= new List<string>();
            }
            return loaded;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "dictionaries.json is corrupt; backing up and restoring defaults.");
            return BackupAndReset();
        }
    }

    private FileModel BackupAndReset()
    {
        try
        {
            var backup = _path + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + ".bak";
            File.Move(_path, backup, overwrite: true);
        }
        catch { /* best effort */ }

        return new FileModel();
    }

    // Idempotent: ensures both built-ins exist and every language has a valid active selection.
    // Returns true if anything changed (so the caller persists). Never overwrites user profiles or a
    // valid user active selection.
    private bool EnsureBuiltInsAndActive(FileModel state)
    {
        var changed = false;
        var now = _clock.GetUtcNow();

        foreach (var lang in SpeechDictionaryProfile.SupportedLanguages)
        {
            var builtInId = SpeechDictionaryProfile.BuiltInIdFor(lang)!.Value;
            if (!state.Profiles.Any(p => p.Id == builtInId))
            {
                state.Profiles.Add(BuildBuiltIn(lang, now));
                changed = true;
            }

            if (!state.ActiveByLanguage.TryGetValue(lang, out var activeId) ||
                !state.Profiles.Any(p => p.Id == activeId))
            {
                state.ActiveByLanguage[lang] = builtInId;
                changed = true;
            }
        }

        return changed;
    }

    private ProfileDto BuildBuiltIn(string lang, DateTimeOffset now)
    {
        _seeds.TryGetValue(lang, out var seed);
        return new ProfileDto
        {
            Id = SpeechDictionaryProfile.BuiltInIdFor(lang)!.Value,
            Name = BuiltInNames.GetValueOrDefault(lang, lang),
            LanguageCode = lang,
            InitialPrompt = string.IsNullOrWhiteSpace(seed?.InitialPrompt) ? null : seed!.InitialPrompt,
            Hotwords = (seed?.Hotwords ?? Array.Empty<string>()).ToList(),
            IsBuiltIn = true,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private void Persist(FileModel state)
    {
        state.SchemaVersion = SchemaVersion;
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(temp, _path, overwrite: true);
    }

    private void LogProfile(string action, ProfileDto dto)
        => _logger.LogInformation(
            "Speech dictionary {Action}: {ProfileId} ({Language}), {HotwordCount} hotwords.",
            action, dto.Id, dto.LanguageCode, dto.Hotwords.Count);

    private static SpeechDictionaryProfile ToDomain(ProfileDto dto) => new()
    {
        Id = dto.Id,
        Name = dto.Name,
        LanguageCode = dto.LanguageCode,
        InitialPrompt = dto.InitialPrompt,
        Hotwords = dto.Hotwords.ToArray(), // defensive copy — callers can never mutate stored state
        IsBuiltIn = dto.IsBuiltIn,
        CreatedAt = dto.CreatedAt,
        UpdatedAt = dto.UpdatedAt
    };

    // ---- persisted shape -------------------------------------------------

    private sealed class FileModel
    {
        [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = SpeechDictionaryStore.SchemaVersion;
        [JsonPropertyName("activeByLanguage")] public Dictionary<string, Guid> ActiveByLanguage { get; set; } = new(StringComparer.Ordinal);
        [JsonPropertyName("profiles")] public List<ProfileDto> Profiles { get; set; } = new();
    }

    private sealed class ProfileDto
    {
        [JsonPropertyName("id")] public Guid Id { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("languageCode")] public string LanguageCode { get; set; } = string.Empty;
        [JsonPropertyName("initialPrompt")] public string? InitialPrompt { get; set; }
        [JsonPropertyName("hotwords")] public List<string> Hotwords { get; set; } = new();
        [JsonPropertyName("isBuiltIn")] public bool IsBuiltIn { get; set; }
        [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; set; }
        [JsonPropertyName("updatedAt")] public DateTimeOffset UpdatedAt { get; set; }
    }
}
