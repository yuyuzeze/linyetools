using KikuCaption.App.Playback;
using Xunit;

namespace KikuCaption.App.Tests.Playback;

public sealed class CaptionSearchTests
{
    private static readonly object FirstTarget = new();
    private static readonly object SecondTarget = new();

    private static CaptionSearchViewModel Create() => new(new[]
    {
        new CaptionSearchSource("10:01:03", "Azure API を確認します", "确认 Azure API", FirstTarget),
        new CaptionSearchSource("10:02:10", "次の議題です", "Next topic", SecondTarget)
    });

    [Fact]
    public void EmptyQuery_ShowsAllCaptionsInOriginalOrder()
    {
        var vm = Create();

        Assert.Equal(2, vm.Results.Count);
        Assert.Same(FirstTarget, vm.Results[0].Target);
        Assert.Same(SecondTarget, vm.Results[1].Target);
        Assert.Same(vm.Results[0], vm.SelectedResult);
    }

    [Fact]
    public void Query_SearchesOriginalAndTranslation()
    {
        var vm = Create();

        vm.Query = "議題";
        Assert.Single(vm.Results);
        Assert.Same(SecondTarget, vm.Results[0].Target);

        vm.Query = "确认";
        Assert.Single(vm.Results);
        Assert.Same(FirstTarget, vm.Results[0].Target);
    }

    [Fact]
    public void EnglishSearch_IsCaseInsensitive_AndNoMatchClearsSelection()
    {
        var vm = Create();

        vm.Query = "NEXT TOPIC";
        Assert.Single(vm.Results);
        Assert.Same(SecondTarget, vm.SelectedResult?.Target);

        vm.Query = "not-present";
        Assert.Empty(vm.Results);
        Assert.Null(vm.SelectedResult);
    }
}
