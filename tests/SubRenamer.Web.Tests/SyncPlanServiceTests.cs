using SubRenamer.Web.Models;
using SubRenamer.Web.Services;
using Xunit;

namespace SubRenamer.Web.Tests;

public sealed class SyncPlanServiceTests : IDisposable
{
    private readonly string _testRoot;
    private readonly string _mediaRoot;
    private readonly string _uploadRoot;
    private readonly SyncPlanService _planService;

    public SyncPlanServiceTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"subrenamer-sync-plan-{Guid.NewGuid():N}");
        _mediaRoot = Path.Combine(_testRoot, "media");
        _uploadRoot = Path.Combine(_testRoot, "uploads");
        Directory.CreateDirectory(_mediaRoot);
        Directory.CreateDirectory(_uploadRoot);
        var safePaths = new SafePathService(new AppPaths(_mediaRoot, _uploadRoot));
        _planService = new SyncPlanService(safePaths, new SubtitleNamingService());
    }

    [Fact]
    public void CreatePlan_GeneratesUniqueLanguageTargetsWithoutWritingFiles()
    {
        var video = CreateFile(_mediaRoot, "Show S01E01.mkv");
        var simplified = CreateFile(_uploadRoot, "Subtitle.01.chs.ass");
        var traditional = CreateFile(_uploadRoot, "Subtitle.01.cht.ass");

        var plan = _planService.CreatePlan(new SyncPlanRequestDto([
            new("01", video, simplified),
            new("01", video, traditional)
        ]));

        Assert.Equal(2, plan.Planned);
        Assert.Equal(0, plan.Invalid);
        Assert.Equal("Show S01E01.chs.ass", plan.Items[0].CandidateFileName);
        Assert.Equal("Show S01E01.cht.ass", plan.Items[1].CandidateFileName);
        Assert.False(plan.Items[0].TargetExists);
        Assert.False(plan.Items[1].TargetExists);
        Assert.False(File.Exists(plan.Items[0].TargetPath));
        Assert.False(File.Exists(plan.Items[1].TargetPath));
    }

    [Fact]
    public void CreatePlan_AddsStableDiscriminatorWhenUnknownNamesCollide()
    {
        var video = CreateFile(_mediaRoot, "Show S01E01.mkv");
        var firstDirectory = Directory.CreateDirectory(Path.Combine(_uploadRoot, "first")).FullName;
        var secondDirectory = Directory.CreateDirectory(Path.Combine(_uploadRoot, "second")).FullName;
        var first = CreateFile(firstDirectory, "Subtitle.01.ass");
        var second = CreateFile(secondDirectory, "Subtitle.01.ass");
        var request = new SyncPlanRequestDto([
            new("01", video, first),
            new("01", video, second)
        ]);

        var firstPlan = _planService.CreatePlan(request);
        var secondPlan = _planService.CreatePlan(request);

        Assert.NotEqual(firstPlan.Items[0].CandidateFileName, firstPlan.Items[1].CandidateFileName);
        Assert.Equal(firstPlan.Items[0].CandidateFileName, secondPlan.Items[0].CandidateFileName);
        Assert.Equal(firstPlan.Items[1].CandidateFileName, secondPlan.Items[1].CandidateFileName);
        Assert.All(firstPlan.Items, item => Assert.StartsWith("Show S01E01.und-01-", item.CandidateFileName));
    }

    [Fact]
    public void CreatePlan_KeepsNamesUniqueWhenSameSubtitleIsRepeated()
    {
        var video = CreateFile(_mediaRoot, "Show S01E01.mkv");
        var subtitle = CreateFile(_uploadRoot, "Subtitle.01.ass");

        var plan = _planService.CreatePlan(new SyncPlanRequestDto([
            new("01", video, subtitle),
            new("01", video, subtitle)
        ]));

        Assert.Equal(2, plan.Planned);
        Assert.NotEqual(plan.Items[0].CandidateFileName, plan.Items[1].CandidateFileName);
    }

    [Fact]
    public void CreatePlan_NormalizesExplicitLanguage()
    {
        var video = CreateFile(_mediaRoot, "Show S01E01.mkv");
        var subtitle = CreateFile(_uploadRoot, "Subtitle.01.ass");

        var plan = _planService.CreatePlan(new SyncPlanRequestDto([
            new("01", video, subtitle, Language: "zh-Hans")
        ]));

        Assert.Equal("chs", plan.Items[0].Language);
        Assert.Equal("Show S01E01.chs.ass", plan.Items[0].CandidateFileName);
    }

    [Fact]
    public void CreatePlan_RequiresReferenceForSubtitleReferenceMode()
    {
        var video = CreateFile(_mediaRoot, "Show S01E01.mkv");
        var subtitle = CreateFile(_uploadRoot, "Subtitle.01.ass");

        var plan = _planService.CreatePlan(new SyncPlanRequestDto([
            new("01", video, subtitle, Mode: SyncMode.SubtitleReference)
        ]));

        Assert.Equal(0, plan.Planned);
        Assert.Equal(1, plan.Invalid);
        Assert.Equal(SyncPlanItemStatus.Invalid, plan.Items[0].Status);
        Assert.Contains("referenceSubtitle", plan.Items[0].Error);
    }

    [Fact]
    public void CreatePlan_InvalidLanguageDoesNotInvalidateSiblingItem()
    {
        var video = CreateFile(_mediaRoot, "Show S01E01.mkv");
        var invalidSubtitle = CreateFile(_uploadRoot, "Subtitle.01.ass");
        var validSubtitle = CreateFile(_uploadRoot, "Subtitle.01.chs.ass");

        var plan = _planService.CreatePlan(new SyncPlanRequestDto([
            new("01", video, invalidSubtitle, Language: "zh/invalid"),
            new("01", video, validSubtitle)
        ]));

        Assert.Equal(SyncPlanItemStatus.Invalid, plan.Items[0].Status);
        Assert.Equal(SyncPlanItemStatus.Planned, plan.Items[1].Status);
        Assert.Equal("Show S01E01.chs.ass", plan.Items[1].CandidateFileName);
    }

    [Fact]
    public void CreatePlan_RejectsMissingSubtitle()
    {
        var video = CreateFile(_mediaRoot, "Show S01E01.mkv");
        var missingSubtitle = Path.Combine(_uploadRoot, "missing.ass");

        var plan = _planService.CreatePlan(new SyncPlanRequestDto([
            new("01", video, missingSubtitle)
        ]));

        Assert.Equal(SyncPlanItemStatus.Invalid, plan.Items[0].Status);
        Assert.Contains("不存在", plan.Items[0].Error);
    }

    [Fact]
    public void CreatePlan_RejectsVideoOutsideMediaRoot()
    {
        var outside = Directory.CreateDirectory(Path.Combine(_testRoot, "outside")).FullName;
        var video = CreateFile(outside, "Show S01E01.mkv");
        var subtitle = CreateFile(_uploadRoot, "Subtitle.01.ass");

        var plan = _planService.CreatePlan(new SyncPlanRequestDto([
            new("01", video, subtitle)
        ]));

        Assert.Equal(SyncPlanItemStatus.Invalid, plan.Items[0].Status);
        Assert.Null(plan.Items[0].TargetPath);
    }

    [Fact]
    public void CreatePlan_ReportsExistingTargetWithoutOverwritingIt()
    {
        var video = CreateFile(_mediaRoot, "Show S01E01.mkv");
        var subtitle = CreateFile(_uploadRoot, "Subtitle.01.chs.ass");
        var existingTarget = CreateFile(_mediaRoot, "Show S01E01.chs.ass", "existing");

        var plan = _planService.CreatePlan(new SyncPlanRequestDto([
            new("01", video, subtitle)
        ]));

        Assert.True(plan.Items[0].TargetExists);
        Assert.Equal("existing", File.ReadAllText(existingTarget));
    }

    private static string CreateFile(string directory, string name, string content = "")
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);
    }
}
