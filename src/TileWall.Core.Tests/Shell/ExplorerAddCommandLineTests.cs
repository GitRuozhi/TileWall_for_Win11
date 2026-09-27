using TileWall.Core.Shell;
using Xunit;

namespace TileWall.Core.Tests.Shell;

public sealed class ExplorerAddCommandLineTests
{
    [Fact]
    public void DirectoryAndUncRoot_TrailingSlashesDoNotAbsorbFollowingItems()
    {
        var result = ExplorerAddCommandLine.Build([@"C:\带 空格\", @"\\server\共享\", @"C:\next.url"]);
        Assert.Equal("--add \"C:\\带 空格\\\\\" \"\\\\server\\共享\\\\\" \"C:\\next.url\"", result.Arguments);
        Assert.Equal(3, result.SelectedCount);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void MultipleTrailingSlashes_AllSurviveWindowsQuoting()
    {
        var result = ExplorerAddCommandLine.Build([@"C:\folder\\"]);
        Assert.Equal("--add \"C:\\folder\\\\\\\\\"", result.Arguments);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void WhitespaceAndUnicode_ArePreserved()
    {
        var result = ExplorerAddCommandLine.Build([@"C:\测试 空格\a.lnk", @"\\主机\共享\b.url"]);
        Assert.Equal("--add \"C:\\测试 空格\\a.lnk\" \"\\\\主机\\共享\\b.url\"", result.Arguments);
        Assert.Equal(2, result.SelectedCount);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void OversizedFirstPath_IsOmittedWholeAndReported()
    {
        var result = ExplorerAddCommandLine.Build([@"C:\" + new string('x', ExplorerAddCommandLine.MaxArgumentsLength)]);
        Assert.Equal(ExplorerAddCommandLine.AddArgument, result.Arguments);
        Assert.Equal(0, result.SelectedCount);
        Assert.True(result.Truncated);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExactlyAtEncodedLimit_FitsButOneMoreCharacterTruncates(bool trailingSlash)
    {
        var pathLength = ExplorerAddCommandLine.MaxArgumentsLength - ExplorerAddCommandLine.AddArgument.Length - 3
            - (trailingSlash ? 1 : 0);
        var path = @"C:\" + new string('x', pathLength - 3 - (trailingSlash ? 1 : 0)) + (trailingSlash ? "\\" : "");
        var exact = ExplorerAddCommandLine.Build([path]);
        Assert.Equal(ExplorerAddCommandLine.MaxArgumentsLength, exact.Arguments.Length);
        Assert.Equal(1, exact.SelectedCount);
        Assert.False(exact.Truncated);

        var longer = ExplorerAddCommandLine.Build([path.Insert(3, "x")]);
        Assert.Equal(ExplorerAddCommandLine.AddArgument, longer.Arguments);
        Assert.Equal(0, longer.SelectedCount);
        Assert.True(longer.Truncated);
    }

    [Fact]
    public void LaterOversizedItem_StopsAtWholePrefixWithoutSkippingAhead()
    {
        var first = @"C:\first folder\";
        var result = ExplorerAddCommandLine.Build([first,
            @"C:\" + new string('x', ExplorerAddCommandLine.MaxArgumentsLength), @"C:\last.lnk"]);
        Assert.Equal(ExplorerAddCommandLine.Build([first]).Arguments, result.Arguments);
        Assert.Equal(1, result.SelectedCount);
        Assert.True(result.Truncated);
    }

    [Fact]
    public void EmptyBatch_HasNoFabricatedPathOrTruncation()
    {
        var result = ExplorerAddCommandLine.Build([]);
        Assert.Equal(ExplorerAddCommandLine.AddArgument, result.Arguments);
        Assert.Equal(0, result.SelectedCount);
        Assert.False(result.Truncated);
    }
}
