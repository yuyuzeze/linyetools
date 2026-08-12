using System.IO;
using System.Linq;
using KikuCaption.App.Localization;
using KikuCaption.App.ViewModels.Pages;
using KikuCaption.Core.Models;
using KikuCaption.Infrastructure.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KikuCaption.App.Tests;

/// <summary>UI-R4B: dictionary page CRUD, validation, active selection and unsaved-changes logic.</summary>
public class DictionaryPageViewModelTests : IDisposable
{
    private readonly string _dir;
    private readonly SpeechDictionaryStore _store;

    public DictionaryPageViewModelTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "kiku_dict_vm", Guid.NewGuid().ToString("N"));
        var seeds = new Dictionary<string, SpeechContext>
        {
            ["ja"] = new("技術会議。", new[] { "Azure" }),
            ["zh"] = new("技术会议。", new[] { "Azure" }),
        };
        _store = new SpeechDictionaryStore(_dir, seeds, NullLogger<SpeechDictionaryStore>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private sealed class FakePrompts : IDictionaryPrompts
    {
        public UnsavedChangesChoice Unsaved = UnsavedChangesChoice.Discard;
        public bool DeleteResult = true;
        public int UnsavedCalls;
        public UnsavedChangesChoice ConfirmUnsavedChanges() { UnsavedCalls++; return Unsaved; }
        public bool ConfirmDelete(bool isActive) => DeleteResult;
    }

    private DictionaryPageViewModel NewVm(FakePrompts? prompts = null)
        => new(_store, new LocalizationService(), prompts ?? new FakePrompts(), NullLogger<DictionaryPageViewModel>.Instance);

    [Fact] // opens on ja, listing the built-in, editor read-only for built-in
    public void Opens_WithBuiltInSelected_ReadOnly()
    {
        var vm = NewVm();
        Assert.Equal("ja", vm.FilterLanguage);
        Assert.Single(vm.Items);
        Assert.True(vm.IsBuiltInSelected);
        Assert.False(vm.IsEditable);
        Assert.False(vm.CanSave);
    }

    [Fact] // New creates an editable, uniquely-named user draft
    public void New_CreatesEditableDraft()
    {
        var vm = NewVm();
        vm.NewCommand.Execute(null);

        Assert.True(vm.IsEditable);
        Assert.False(vm.IsBuiltInSelected);
        Assert.Equal("ja", vm.EditLanguage);
        Assert.False(string.IsNullOrWhiteSpace(vm.EditName));
    }

    [Fact] // saving a valid draft persists it and selects it in the list
    public void Save_PersistsAndSelects()
    {
        var vm = NewVm();
        vm.NewCommand.Execute(null);
        vm.EditName = "融资业务";
        vm.EditInitialPrompt = "融资会议。";
        vm.EditHotwords = "IPO\nM&A\nIPO"; // duplicate collapses
        Assert.True(vm.CanSave);
        vm.SaveCommand.Execute(null);

        var saved = _store.GetProfiles("ja").FirstOrDefault(p => p.Name == "融资业务");
        Assert.NotNull(saved);
        Assert.Equal(new[] { "IPO", "M&A" }, saved!.Hotwords);
        Assert.Equal(saved.Id, vm.SelectedItem!.Id);
    }

    [Fact] // an empty name blocks saving with a validation message
    public void EmptyName_BlocksSave()
    {
        var vm = NewVm();
        vm.NewCommand.Execute(null);
        vm.EditName = "   ";
        Assert.False(vm.CanSave);
        Assert.NotEqual(string.Empty, vm.ValidationError);
    }

    [Fact] // a duplicate name within the language is flagged live
    public void DuplicateName_IsFlagged()
    {
        var vm = NewVm();
        _store.Upsert(new SpeechDictionaryProfile { Id = Guid.NewGuid(), Name = "会议", LanguageCode = "ja" });
        vm.NewCommand.Execute(null);
        vm.EditName = "会议";
        Assert.False(vm.CanSave);
    }

    [Fact] // more than 64 hotwords blocks saving
    public void TooManyHotwords_BlocksSave()
    {
        var vm = NewVm();
        vm.NewCommand.Execute(null);
        vm.EditName = "big";
        vm.EditHotwords = string.Join("\n", Enumerable.Range(0, Hotwords.MaxCount + 1).Select(i => "t" + i));
        Assert.False(vm.CanSave);
        Assert.NotEqual(string.Empty, vm.ValidationError);
    }

    [Fact] // live count labels reflect the cleaned hotwords
    public void CountLabels_Update()
    {
        var vm = NewVm();
        vm.NewCommand.Execute(null);
        vm.EditName = "c";
        vm.EditHotwords = "Azure\nOpenAI";
        Assert.Contains("2/", vm.HotwordCountText);
    }

    [Fact] // Duplicate copies content into a new user dictionary with a distinct id/name
    public void Duplicate_CopiesContent()
    {
        var vm = NewVm(); // built-in ja selected
        vm.DuplicateCommand.Execute(null);
        vm.EditName = vm.EditName; // no-op; keep generated unique name
        var beforeCount = _store.GetProfiles("ja").Count;
        vm.SaveCommand.Execute(null);

        var after = _store.GetProfiles("ja");
        Assert.Equal(beforeCount + 1, after.Count);
        var copy = after.First(p => !p.IsBuiltIn);
        Assert.Equal("技術会議。", copy.InitialPrompt); // content copied from the built-in
    }

    [Fact] // Set active marks the row and reports "next meeting"
    public void SetActive_UpdatesMarkerAndStatus()
    {
        var vm = NewVm();
        vm.NewCommand.Execute(null);
        vm.EditName = "active-me";
        vm.SaveCommand.Execute(null);

        vm.SetActiveCommand.Execute(null);

        Assert.Equal(vm.SelectedItem!.Id, _store.GetActiveId("ja"));
        Assert.True(vm.SelectedItem.IsActive);
        Assert.Equal(new LocalizationService()["Dict.AppliesNextMeeting"], vm.StatusText);
    }

    [Fact] // deleting the active user dictionary falls back to the built-in
    public void DeleteActive_FallsBack()
    {
        var vm = NewVm(new FakePrompts { DeleteResult = true });
        vm.NewCommand.Execute(null);
        vm.EditName = "gone";
        vm.SaveCommand.Execute(null);
        vm.SetActiveCommand.Execute(null);

        vm.DeleteCommand.Execute(null);

        Assert.Equal(SpeechDictionaryProfile.BuiltInJapaneseId, _store.GetActiveId("ja"));
    }

    [Fact] // the language filter isolates ja and zh listings
    public void LanguageFilter_IsolatesLists()
    {
        _store.Upsert(new SpeechDictionaryProfile { Id = Guid.NewGuid(), Name = "zh-only", LanguageCode = "zh" });
        var vm = NewVm();

        Assert.DoesNotContain(vm.Items, i => i.RawName == "zh-only");
        vm.FilterLanguage = "zh";
        Assert.Contains(vm.Items, i => i.RawName == "zh-only");
    }

    [Fact] // switching away from an unsaved draft prompts; Cancel keeps the edit
    public void UnsavedChanges_CancelKeepsEditing()
    {
        var prompts = new FakePrompts { Unsaved = UnsavedChangesChoice.Cancel };
        var vm = NewVm(prompts);
        vm.NewCommand.Execute(null);
        vm.EditName = "dirty-draft";

        vm.FilterLanguage = "zh"; // attempt to leave

        Assert.Equal(1, prompts.UnsavedCalls);
        Assert.Equal("ja", vm.FilterLanguage);      // reverted
        Assert.Equal("dirty-draft", vm.EditName);   // edit preserved
    }

    [Fact] // Discard lets the switch proceed and drops the draft
    public void UnsavedChanges_DiscardProceeds()
    {
        var prompts = new FakePrompts { Unsaved = UnsavedChangesChoice.Discard };
        var vm = NewVm(prompts);
        vm.NewCommand.Execute(null);
        vm.EditName = "dirty-draft";

        vm.FilterLanguage = "zh";

        Assert.Equal("zh", vm.FilterLanguage);
        Assert.DoesNotContain(_store.GetProfiles(), p => p.Name == "dirty-draft"); // not saved
    }

    [Fact] // a clean built-in selection does not trigger the unsaved prompt
    public void CleanSelection_NoPrompt()
    {
        var prompts = new FakePrompts();
        var vm = NewVm(prompts);
        vm.FilterLanguage = "zh";
        Assert.Equal(0, prompts.UnsavedCalls);
    }

    // ---- built-in localization (UI-R4B follow-up item 1) ----------------

    private DictionaryPageViewModel NewVm(LocalizationService loc, FakePrompts? prompts = null)
        => new(_store, loc, prompts ?? new FakePrompts(), NullLogger<DictionaryPageViewModel>.Instance);

    [Theory] // the built-in detail name is localized in all three UI languages
    [InlineData(LocalizedStrings.ZhCN, "默认日语技术词典")]
    [InlineData(LocalizedStrings.EnUS, "Default Japanese technical dictionary")]
    [InlineData(LocalizedStrings.JaJP, "既定の日本語技術辞書")]
    public void BuiltInDetailName_IsLocalized(string culture, string expected)
    {
        var loc = new LocalizationService();
        loc.SetLanguage(culture);
        var vm = NewVm(loc); // opens on the ja built-in
        Assert.Equal(expected, vm.EditName);
    }

    [Fact] // the list row name and the editor name for a built-in are the same localized string
    public void BuiltIn_ListAndDetailName_AreConsistent()
    {
        var loc = new LocalizationService();
        loc.SetLanguage(LocalizedStrings.EnUS);
        var vm = NewVm(loc);
        Assert.Equal(vm.SelectedItem!.DisplayName, vm.EditName);
    }

    [Fact] // switching the UI language refreshes list + detail without touching the persisted file
    public void SwitchingLanguage_RefreshesNames_DoesNotWriteFile()
    {
        var loc = new LocalizationService();
        var vm = NewVm(loc); // zh-CN default, ja built-in selected
        Assert.Equal("默认日语技术词典", vm.EditName);

        var path = Path.Combine(_dir, "dictionaries.json");
        var before = File.ReadAllText(path);
        var beforeStamp = File.GetLastWriteTimeUtc(path);

        loc.SetLanguage(LocalizedStrings.JaJP);

        Assert.Equal("既定の日本語技術辞書", vm.EditName);                 // detail refreshed
        Assert.Equal("既定の日本語技術辞書", vm.SelectedItem!.DisplayName); // list refreshed
        Assert.Equal(before, File.ReadAllText(path));                       // file unchanged
        Assert.Equal(beforeStamp, File.GetLastWriteTimeUtc(path));
    }

    [Fact] // a user dictionary name is never re-localized when the UI language changes
    public void UserDictionaryName_IsNotTranslated()
    {
        var loc = new LocalizationService();
        var vm = NewVm(loc);
        vm.NewCommand.Execute(null);
        vm.EditName = "融资业务";
        vm.SaveCommand.Execute(null);

        loc.SetLanguage(LocalizedStrings.EnUS);

        Assert.Equal("融资业务", vm.SelectedItem!.DisplayName);
        Assert.Equal("融资业务", vm.EditName);
    }

    // ---- built-in read-only (UI-R4B follow-up item 2) -------------------

    [Fact] // a built-in is read-only: not editable and cannot be saved or deleted
    public void BuiltIn_IsReadOnly_NoSaveNoDelete()
    {
        var vm = NewVm(); // ja built-in selected
        Assert.False(vm.IsEditable);
        Assert.False(vm.CanSave);

        vm.SaveCommand.Execute(null); // no-op for a built-in
        vm.DeleteCommand.Execute(null);

        Assert.NotNull(_store.GetById(SpeechDictionaryProfile.BuiltInJapaneseId)); // still present
        Assert.Equal(2, _store.GetProfiles().Count);
    }

    [Fact] // duplicating a built-in yields an editable user dictionary
    public void DuplicatingBuiltIn_YieldsEditableUserDictionary()
    {
        var vm = NewVm();
        vm.DuplicateCommand.Execute(null);
        Assert.True(vm.IsEditable);
        Assert.False(vm.IsBuiltInSelected);
        vm.SaveCommand.Execute(null);

        var copy = _store.GetProfiles("ja").First(p => !p.IsBuiltIn);
        Assert.False(copy.IsBuiltIn);
    }

    [Fact] // "Set active" and "Duplicate" remain available on a built-in selection
    public void BuiltIn_SetActiveAndDuplicate_RemainAvailable()
    {
        var vm = NewVm();
        Assert.True(vm.HasSelection);       // enables Set active + Duplicate in the view
        Assert.True(vm.IsBuiltInSelected);  // Restore-default is shown only here
    }
}
