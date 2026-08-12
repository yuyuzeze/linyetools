using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KikuCaption.App.Localization;
using KikuCaption.Core.Interfaces;
using KikuCaption.Core.Models;
using Microsoft.Extensions.Logging;

namespace KikuCaption.App.ViewModels.Pages;

/// <summary>What the user chose when leaving a dictionary with unsaved edits.</summary>
public enum UnsavedChangesChoice { Save, Discard, Cancel }

/// <summary>
/// UI prompts the dictionary page needs (confirm delete, unsaved-changes fork). Abstracted so the
/// view model stays headless and unit-testable; the WPF implementation uses <c>MessageBox</c>.
/// </summary>
public interface IDictionaryPrompts
{
    UnsavedChangesChoice ConfirmUnsavedChanges();
    bool ConfirmDelete(bool isActive);
}

/// <summary>A row in the dictionary list. Built-in rows show a localized name; user rows their own.</summary>
public sealed partial class DictionaryListItem : ObservableObject
{
    public required Guid Id { get; init; }
    public required string LanguageCode { get; init; }
    public required bool IsBuiltIn { get; init; }
    public required string RawName { get; init; }

    [ObservableProperty] private string _displayName = string.Empty;
    [ObservableProperty] private bool _isActive;
}

/// <summary>
/// Dictionary page (UI-R4B): manage per-language speech-recognition dictionaries (initial prompt +
/// hotwords) for the local faster-whisper worker. Create / copy / rename / edit / delete user
/// dictionaries and mark one active per language; built-ins are read-only and never deleted.
///
/// Switching the active dictionary takes effect at the NEXT meeting (a running session keeps the
/// dictionary it snapshotted at start). Edits are explicit — leaving a dictionary with unsaved
/// changes prompts Save / Discard / Cancel, never a silent loss. All persistence goes through
/// <see cref="ISpeechDictionaryStore"/>; this view model never touches files, SQLite, or the
/// translation API.
/// </summary>
public sealed partial class DictionaryPageViewModel : ObservableObject
{
    private readonly ISpeechDictionaryStore _store;
    private readonly LocalizationService _loc;
    private readonly IDictionaryPrompts _prompts;
    private readonly ILogger<DictionaryPageViewModel> _logger;

    private bool _suppress; // guards programmatic field writes from the dirty/validation machinery
    private Guid _editingId;
    private bool _editingIsBuiltIn;
    private bool _hasEditor;
    private string _baselineName = string.Empty;
    private string _baselinePrompt = string.Empty;
    private string _baselineHotwords = string.Empty;

    public DictionaryPageViewModel(
        ISpeechDictionaryStore store,
        LocalizationService loc,
        IDictionaryPrompts prompts,
        ILogger<DictionaryPageViewModel> logger)
    {
        _store = store;
        _loc = loc;
        _prompts = prompts;
        _logger = logger;
        _loc.LanguageChanged += (_, _) => OnLanguageChanged();
        LoadList(selectActive: true);
    }

    public IReadOnlyList<string> FilterLanguages { get; } = SpeechDictionaryProfile.SupportedLanguages;

    public ObservableCollection<DictionaryListItem> Items { get; } = new();

    [ObservableProperty] private string _filterLanguage = "ja";
    [ObservableProperty] private DictionaryListItem? _selectedItem;
    [ObservableProperty] private bool _hasSelection;

    [ObservableProperty] private string _editName = string.Empty;
    [ObservableProperty] private string _editLanguage = "ja";
    [ObservableProperty] private string _editInitialPrompt = string.Empty;
    [ObservableProperty] private string _editHotwords = string.Empty;

    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private string _validationError = string.Empty;

    // Computed display — refreshed by Revalidate()/OnLanguageChanged().
    [ObservableProperty] private string _hotwordCountText = string.Empty;
    [ObservableProperty] private string _hotwordCharText = string.Empty;
    [ObservableProperty] private string _promptCharText = string.Empty;
    [ObservableProperty] private bool _isEditorVisible;
    [ObservableProperty] private bool _isEditable;   // false for built-in (read-only view)
    [ObservableProperty] private bool _isBuiltInSelected;
    [ObservableProperty] private bool _canSave;

    // ---- filter / selection ---------------------------------------------

    partial void OnFilterLanguageChanged(string value)
    {
        if (_suppress)
        {
            return;
        }

        if (!TryLeaveEditor())
        {
            // Revert the combo without re-triggering the guard.
            _suppress = true;
            FilterLanguage = EditLanguage;
            _suppress = false;
            return;
        }

        LoadList(selectActive: true);
    }

    partial void OnSelectedItemChanged(DictionaryListItem? value)
    {
        HasSelection = value is not null;
        if (_suppress || value is null)
        {
            return;
        }

        if (!TryLeaveEditor())
        {
            return; // stay on the previous editor; selection is reconciled by LoadEditor on success
        }

        LoadEditor(value.Id);
    }

    // ---- editor field change tracking -----------------------------------

    partial void OnEditNameChanged(string value) => Revalidate();
    partial void OnEditInitialPromptChanged(string value) => Revalidate();
    partial void OnEditHotwordsChanged(string value) => Revalidate();

    // ---- commands --------------------------------------------------------

    [RelayCommand]
    private void New()
    {
        if (!TryLeaveEditor())
        {
            return;
        }

        var name = UniqueName(_loc["Dict.NewName"], FilterLanguage, Guid.Empty);
        LoadEditorFrom(Guid.Empty, FilterLanguage, name, string.Empty, string.Empty, isBuiltIn: false, isNew: true);
        _suppress = true;
        SelectedItem = null;
        _suppress = false;
    }

    [RelayCommand]
    private void Duplicate()
    {
        var source = SelectedItem is null ? null : _store.GetById(SelectedItem.Id);
        if (source is null || !TryLeaveEditor())
        {
            return;
        }

        var baseName = $"{DisplayNameFor(source)} {_loc["Dict.CopySuffix"]}";
        var name = UniqueName(baseName, source.LanguageCode, Guid.Empty);
        LoadEditorFrom(Guid.Empty, source.LanguageCode, name, source.InitialPrompt ?? string.Empty,
            string.Join(Environment.NewLine, source.Hotwords), isBuiltIn: false, isNew: true);
        _suppress = true;
        SelectedItem = null;
        _suppress = false;
    }

    [RelayCommand]
    private void Save()
    {
        if (!_hasEditor || _editingIsBuiltIn)
        {
            return;
        }

        try
        {
            var profile = new SpeechDictionaryProfile
            {
                Id = _editingId,
                Name = EditName,
                LanguageCode = EditLanguage,
                InitialPrompt = string.IsNullOrWhiteSpace(EditInitialPrompt) ? null : EditInitialPrompt,
                Hotwords = ParseHotwords(EditHotwords)
            };

            var saved = _store.Upsert(profile);
            _editingId = saved.Id;
            SetBaseline(saved.Name, saved.InitialPrompt ?? string.Empty, string.Join(Environment.NewLine, saved.Hotwords));
            LoadList(selectId: saved.Id);
            StatusText = _loc["Dict.Saved"];
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            ValidationError = ex.Message;
            CanSave = false;
        }
    }

    [RelayCommand]
    private void SetActive()
    {
        if (SelectedItem is null)
        {
            return;
        }

        _store.SetActive(SelectedItem.LanguageCode, SelectedItem.Id);
        RefreshActiveMarkers();
        StatusText = _loc["Dict.AppliesNextMeeting"];
    }

    [RelayCommand]
    private void Delete()
    {
        if (SelectedItem is null || SelectedItem.IsBuiltIn)
        {
            return;
        }

        var isActive = _store.GetActiveId(SelectedItem.LanguageCode) == SelectedItem.Id;
        if (!_prompts.ConfirmDelete(isActive))
        {
            return;
        }

        var id = SelectedItem.Id;
        _store.Delete(id);
        DiscardEditor();
        LoadList(selectActive: true);
        StatusText = _loc["Dict.Deleted"];
        _logger.LogInformation("User deleted speech dictionary {ProfileId}.", id);
    }

    [RelayCommand]
    private void RestoreBuiltIn()
    {
        if (!_editingIsBuiltIn)
        {
            return;
        }

        _store.RestoreBuiltInDefaults();
        var id = _editingId;
        LoadList(selectId: id);
        StatusText = _loc["Dict.Restored"];
    }

    // ---- loading ---------------------------------------------------------

    private void LoadList(bool selectActive = false, Guid? selectId = null)
    {
        var profiles = _store.GetProfiles(FilterLanguage);
        var activeId = _store.GetActiveId(FilterLanguage);

        _suppress = true;
        Items.Clear();
        foreach (var p in profiles.OrderByDescending(p => p.IsBuiltIn).ThenBy(p => p.Name, StringComparer.CurrentCulture))
        {
            Items.Add(new DictionaryListItem
            {
                Id = p.Id,
                LanguageCode = p.LanguageCode,
                IsBuiltIn = p.IsBuiltIn,
                RawName = p.Name,
                DisplayName = DisplayNameFor(p),
                IsActive = p.Id == activeId
            });
        }
        _suppress = false;

        var target = selectId is not null
            ? Items.FirstOrDefault(i => i.Id == selectId.Value)
            : selectActive
                ? Items.FirstOrDefault(i => i.Id == activeId) ?? Items.FirstOrDefault()
                : null;

        _suppress = true;
        SelectedItem = target;
        _suppress = false;
        HasSelection = target is not null;

        if (target is not null)
        {
            LoadEditor(target.Id);
        }
        else
        {
            DiscardEditor();
        }
    }

    private void LoadEditor(Guid id)
    {
        var p = _store.GetById(id);
        if (p is null)
        {
            DiscardEditor();
            return;
        }

        // Built-ins show their localized name (read-only); user dictionaries show their own name.
        LoadEditorFrom(p.Id, p.LanguageCode, DisplayNameFor(p), p.InitialPrompt ?? string.Empty,
            string.Join(Environment.NewLine, p.Hotwords), p.IsBuiltIn, isNew: false);
    }

    private void LoadEditorFrom(Guid id, string language, string name, string prompt, string hotwords, bool isBuiltIn, bool isNew)
    {
        _suppress = true;
        _editingId = id;
        _editingIsBuiltIn = isBuiltIn;
        _hasEditor = true;
        EditLanguage = language;
        EditName = name;
        EditInitialPrompt = prompt;
        EditHotwords = hotwords;
        // A brand-new profile is considered dirty until saved (baseline left blank).
        SetBaseline(isNew ? "\0new" : name, isNew ? string.Empty : prompt, isNew ? string.Empty : hotwords);
        IsEditorVisible = true;
        IsEditable = !isBuiltIn;
        IsBuiltInSelected = isBuiltIn;
        _suppress = false;

        StatusText = string.Empty;
        Revalidate();
    }

    private void DiscardEditor()
    {
        _suppress = true;
        _hasEditor = false;
        _editingId = Guid.Empty;
        _editingIsBuiltIn = false;
        EditName = string.Empty;
        EditInitialPrompt = string.Empty;
        EditHotwords = string.Empty;
        IsEditorVisible = false;
        IsEditable = false;
        IsBuiltInSelected = false;
        ValidationError = string.Empty;
        CanSave = false;
        _suppress = false;
    }

    // ---- unsaved-changes guard ------------------------------------------

    /// <summary>Public entry for the shell/window to intercept navigation-away and close.</summary>
    public bool TryLeaveEditor()
    {
        if (!IsDirty())
        {
            return true;
        }

        switch (_prompts.ConfirmUnsavedChanges())
        {
            case UnsavedChangesChoice.Save:
                Save();
                return ValidationError.Length == 0; // a failed save keeps the user on the editor
            case UnsavedChangesChoice.Discard:
                return true;
            default:
                return false;
        }
    }

    private bool IsDirty()
    {
        if (!_hasEditor || _editingIsBuiltIn)
        {
            return false;
        }

        return EditName != _baselineName
            || EditInitialPrompt != _baselinePrompt
            || EditHotwords != _baselineHotwords;
    }

    private void SetBaseline(string name, string prompt, string hotwords)
    {
        _baselineName = name;
        _baselinePrompt = prompt;
        _baselineHotwords = hotwords;
    }

    // ---- validation + live counts ---------------------------------------

    private void Revalidate()
    {
        if (_suppress)
        {
            return;
        }

        var terms = ParseHotwords(EditHotwords);
        var totalChars = terms.Sum(t => t.Length);
        HotwordCountText = $"{_loc["Dict.ItemsLabel"]} {terms.Count}/{Hotwords.MaxCount}";
        HotwordCharText = $"{_loc["Dict.CharsLabel"]} {totalChars}/{Hotwords.MaxTotalCharacters}";
        var promptLen = (EditInitialPrompt ?? string.Empty).Trim().Length;
        PromptCharText = $"{_loc["Dict.PromptCharsLabel"]} {promptLen}/{SpeechDictionaryProfile.MaxInitialPromptLength}";

        if (_editingIsBuiltIn || !_hasEditor)
        {
            ValidationError = string.Empty;
            CanSave = false;
            return;
        }

        ValidationError = ComputeError(terms, totalChars, promptLen);
        CanSave = ValidationError.Length == 0;
    }

    private string ComputeError(IReadOnlyList<string> terms, int totalChars, int promptLen)
    {
        var name = (EditName ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            return _loc["Dict.Error.NameRequired"];
        }

        if (name.Length > SpeechDictionaryProfile.MaxNameLength)
        {
            return string.Format(_loc["Dict.Error.NameLength"], name.Length, SpeechDictionaryProfile.MaxNameLength);
        }

        // Per-language name uniqueness (case/trim-insensitive), excluding the profile being edited.
        var clash = _store.GetProfiles(EditLanguage).Any(p =>
            p.Id != _editingId &&
            string.Equals(p.Name.Trim(), name, StringComparison.OrdinalIgnoreCase));
        if (clash)
        {
            return string.Format(_loc["Dict.Error.NameDuplicate"], name);
        }

        var over = terms.FirstOrDefault(t => t.Length > Hotwords.MaxTermLength);
        if (over is not null)
        {
            return string.Format(_loc["Dict.Error.HotwordLength"], Truncate(over), over.Length, Hotwords.MaxTermLength);
        }

        if (terms.Count > Hotwords.MaxCount)
        {
            return string.Format(_loc["Dict.Error.HotwordCount"], terms.Count, Hotwords.MaxCount);
        }

        if (totalChars > Hotwords.MaxTotalCharacters)
        {
            return string.Format(_loc["Dict.Error.HotwordTotal"], totalChars, Hotwords.MaxTotalCharacters);
        }

        if (promptLen > SpeechDictionaryProfile.MaxInitialPromptLength)
        {
            return string.Format(_loc["Dict.Error.PromptLength"], promptLen, SpeechDictionaryProfile.MaxInitialPromptLength);
        }

        return string.Empty;
    }

    // ---- helpers ---------------------------------------------------------

    /// <summary>Splits editor text into cleaned terms: trim, drop empty, dedup (order preserved).</summary>
    private static IReadOnlyList<string> ParseHotwords(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<string>();
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var term = raw.Trim();
            if (term.Length > 0 && seen.Add(term))
            {
                result.Add(term);
            }
        }

        return result;
    }

    private string UniqueName(string baseName, string language, Guid excludeId)
    {
        var existing = _store.GetProfiles(language)
            .Where(p => p.Id != excludeId)
            .Select(p => p.Name.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!existing.Contains(baseName.Trim()))
        {
            return baseName;
        }

        for (var n = 2; ; n++)
        {
            var candidate = $"{baseName} {n}";
            if (!existing.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private string DisplayNameFor(SpeechDictionaryProfile p)
        => p.IsBuiltIn ? _loc[$"Dict.BuiltIn.{p.LanguageCode}"] : p.Name;

    private void RefreshActiveMarkers()
    {
        var activeId = _store.GetActiveId(FilterLanguage);
        foreach (var item in Items)
        {
            item.IsActive = item.Id == activeId;
        }
    }

    private void OnLanguageChanged()
    {
        // Built-in display names are localized, so the list AND the open editor must both refresh
        // when the UI language changes. User dictionary names are never re-localized.
        foreach (var item in Items)
        {
            if (item.IsBuiltIn)
            {
                item.DisplayName = _loc[$"Dict.BuiltIn.{item.LanguageCode}"];
            }
        }

        if (_hasEditor && _editingIsBuiltIn)
        {
            _suppress = true;
            EditName = _loc[$"Dict.BuiltIn.{EditLanguage}"];
            _baselineName = EditName;
            _suppress = false;
        }

        Revalidate();
    }

    private static string Truncate(string s) => s.Length <= 12 ? s : s[..12] + "…";
}
