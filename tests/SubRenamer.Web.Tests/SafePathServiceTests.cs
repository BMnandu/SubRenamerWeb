using SubRenamer.Web.Services;
using Xunit;

namespace SubRenamer.Web.Tests;

public sealed class SafePathServiceTests : IDisposable
{
    private readonly string _testRoot;
    private readonly string _mediaRoot;
    private readonly string _uploadRoot;
    private readonly SafePathService _safePaths;

    public SafePathServiceTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"subrenamer-safe-path-{Guid.NewGuid():N}");
        _mediaRoot = Path.Combine(_testRoot, "media");
        _uploadRoot = Path.Combine(_testRoot, "uploads");
        Directory.CreateDirectory(_mediaRoot);
        Directory.CreateDirectory(_uploadRoot);
        _safePaths = new SafePathService(new AppPaths(_mediaRoot, _uploadRoot));
    }

    [Fact]
    public void EnsureMediaPath_AcceptsPathInsideMediaRoot()
    {
        var path = Path.Combine(_mediaRoot, "Show", "Episode01.mkv");

        Assert.Equal(Path.GetFullPath(path), _safePaths.EnsureMediaPath(path));
    }

    [Fact]
    public void EnsureMediaPath_RejectsSiblingWithSamePrefix()
    {
        var sibling = $"{_mediaRoot}-evil";

        Assert.Throws<UnauthorizedAccessException>(() =>
            _safePaths.EnsureMediaPath(Path.Combine(sibling, "Episode01.mkv")));
    }

    [Fact]
    public void ResolveMediaSubdirectory_RejectsParentTraversal()
    {
        Assert.Throws<UnauthorizedAccessException>(() =>
            _safePaths.ResolveMediaSubdirectory("../outside"));
    }

    [Fact]
    public void ResolveMediaSubdirectory_RejectsAbsolutePath()
    {
        Assert.Throws<UnauthorizedAccessException>(() =>
            _safePaths.ResolveMediaSubdirectory(Path.Combine(_testRoot, "outside")));
    }

    [Fact]
    public void EnsureMediaPath_RejectsSymbolicLinkEscape()
    {
        var outside = Path.Combine(_testRoot, "outside");
        var link = Path.Combine(_mediaRoot, "linked-outside");
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(link, outside);

        Assert.Throws<UnauthorizedAccessException>(() =>
            _safePaths.EnsureMediaPath(Path.Combine(link, "subtitle.ass")));
    }

    [Fact]
    public void EnsureMediaPath_RejectsDanglingSymbolicLink()
    {
        var missingOutside = Path.Combine(_testRoot, "missing-outside");
        var link = Path.Combine(_mediaRoot, "dangling-link");
        Directory.CreateSymbolicLink(link, missingOutside);

        Assert.Throws<UnauthorizedAccessException>(() =>
            _safePaths.EnsureMediaPath(Path.Combine(link, "subtitle.ass")));
    }

    [Fact]
    public void FileScanService_DoesNotFollowSymbolicLinkOutsideMediaRoot()
    {
        var outside = Path.Combine(_testRoot, "outside-scan");
        var outsideVideo = Path.Combine(outside, "Outside.mkv");
        var link = Path.Combine(_mediaRoot, "linked-library");
        Directory.CreateDirectory(outside);
        File.WriteAllBytes(outsideVideo, []);
        Directory.CreateSymbolicLink(link, outside);
        var scanService = new FileScanService(_safePaths);

        var result = scanService.Scan(null);

        Assert.Empty(result.Videos);
        Assert.Empty(result.Subtitles);
    }

    [Theory]
    [InlineData("../session")]
    [InlineData("bad/session")]
    [InlineData("bad\\session")]
    [InlineData("session with spaces")]
    public void ResolveUploadSessionDirectory_RejectsInvalidSessionId(string sessionId)
    {
        Assert.Throws<ArgumentException>(() =>
            _safePaths.ResolveUploadSessionDirectory(sessionId));
    }

    [Theory]
    [InlineData("../subtitle.ass")]
    [InlineData("folder/subtitle.ass")]
    [InlineData("folder\\subtitle.ass")]
    public void ResolveUploadFile_RejectsPathComponents(string fileName)
    {
        Assert.Throws<ArgumentException>(() =>
            _safePaths.ResolveUploadFile("session-01", fileName));
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);
    }
}
