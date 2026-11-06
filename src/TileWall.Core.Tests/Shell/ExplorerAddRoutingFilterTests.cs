using Xunit;

using TileWall.Core.Shell;

namespace TileWall.Core.Tests.Shell;

/// <summary>WM_COPYDATA 声明 HWND 映像路由过滤——魔数、映像路径一致性、综合结果。</summary>
public sealed class ExplorerAddRoutingFilterTests
{
    private const string SelfPath = @"C:\Apps\TileWall\TileWall.exe";
    private static readonly ExplorerAddProcessIdentity Self = new(SelfPath, "S-1-5-21-1000", 1, true);

    [Fact]
    public void 私有魔数为_TWl1_的小端数值()
    {
        Assert.Equal(0x314C5754u, ExplorerAddRoutingFilter.Magic); // 'T'54 'W'57 'L'4C '1'31 little-endian
        Assert.True(ExplorerAddRoutingFilter.HasMagic(ExplorerAddRoutingFilter.Magic));
        Assert.False(ExplorerAddRoutingFilter.HasMagic(0));
        Assert.False(ExplorerAddRoutingFilter.HasMagic(ExplorerAddRoutingFilter.Magic ^ 0x1)); // 最低位扰动即失配
    }

    [Fact]
    public void 魔数不符_过滤拒绝()
    {
        Assert.Equal(
            ExplorerAddRoutingVerdict.RejectBadMagic,
            ExplorerAddRoutingFilter.Evaluate(0, Self, Self));
        Assert.Equal(
            ExplorerAddRoutingVerdict.RejectBadMagic,
            ExplorerAddRoutingFilter.Evaluate(uint.MaxValue, Self, Self));
    }

    [Fact]
    public void 同一映像路径_通过路由过滤()
    {
        Assert.Equal(ExplorerAddRoutingVerdict.Accept, ExplorerAddRoutingFilter.Evaluate(ExplorerAddRoutingFilter.Magic, Self, Self));
    }

    [Fact]
    public void 映像比对_大小写不敏感()
    {
        Assert.True(ExplorerAddRoutingFilter.DeclaredWindowImageMatches(@"c:\apps\tilewall\tilewall.exe", @"C:\APPS\TILEWALL\TILEWALL.EXE"));
        Assert.Equal(
            ExplorerAddRoutingVerdict.Accept,
            ExplorerAddRoutingFilter.Evaluate(ExplorerAddRoutingFilter.Magic, Self with { ImagePath = @"c:\apps\tilewall\tilewall.exe" }, Self));
    }

    [Fact]
    public void 映像比对_尾部分隔符容忍()
    {
        Assert.True(ExplorerAddRoutingFilter.DeclaredWindowImageMatches(SelfPath + @"\", SelfPath));
        Assert.True(ExplorerAddRoutingFilter.DeclaredWindowImageMatches(SelfPath, SelfPath + "/")); // 正斜杠同样容忍
    }

    [Fact]
    public void 映像路径不一致_过滤拒绝()
    {
        Assert.Equal(
            ExplorerAddRoutingVerdict.RejectImagePathMismatch,
            ExplorerAddRoutingFilter.Evaluate(ExplorerAddRoutingFilter.Magic,
                Self with { ImagePath = @"D:\OtherCopy\TileWall.exe", IsTileWall = false }, Self));
        Assert.False(ExplorerAddRoutingFilter.DeclaredWindowImageMatches(@"C:\Apps\TileWall\TileWall2.exe", SelfPath));
        Assert.False(ExplorerAddRoutingFilter.DeclaredWindowImageMatches(@"C:\Apps\TileWall\TileWall.exe.bak", SelfPath));
    }

    [Fact]
    public void 空路径_保守拒绝()
    {
        Assert.False(ExplorerAddRoutingFilter.DeclaredWindowImageMatches(null, SelfPath));
        Assert.False(ExplorerAddRoutingFilter.DeclaredWindowImageMatches(string.Empty, SelfPath));
        Assert.False(ExplorerAddRoutingFilter.DeclaredWindowImageMatches(" ", SelfPath));
        Assert.Equal(
            ExplorerAddRoutingVerdict.RejectProcessUnavailable,
            ExplorerAddRoutingFilter.Evaluate(ExplorerAddRoutingFilter.Magic, null, Self));
        Assert.Equal(
            ExplorerAddRoutingVerdict.RejectSelfImageUnavailable,
            ExplorerAddRoutingFilter.Evaluate(ExplorerAddRoutingFilter.Magic, Self, null));
    }

    [Fact]
    public void DifferentInstallationOfSameProductInSameUserAndSessionIsAccepted()
    {
        var installed = Self with { ImagePath = @"C:\Program Files\WindowsApps\Ruozhi.TileWall_1.3.6.0_x64\TileWall.exe" };
        Assert.Equal(ExplorerAddRoutingVerdict.Accept,
            ExplorerAddRoutingFilter.Evaluate(ExplorerAddRoutingFilter.Magic, installed, Self));
    }

    [Fact]
    public void SameExecutableInAnotherUserOrLoginSessionIsRejected()
    {
        Assert.Equal(ExplorerAddRoutingVerdict.RejectUserMismatch,
            ExplorerAddRoutingFilter.Evaluate(ExplorerAddRoutingFilter.Magic,
                Self with { UserSid = "S-1-5-21-2000" }, Self));
        Assert.Equal(ExplorerAddRoutingVerdict.RejectSessionMismatch,
            ExplorerAddRoutingFilter.Evaluate(ExplorerAddRoutingFilter.Magic,
                Self with { SessionId = 2 }, Self));
    }

    [Theory]
    [InlineData("", 1)]
    [InlineData("S-1-5-21-1000", -1)]
    public void IncompleteProcessObservationIsRejected(string sid, int session)
    {
        Assert.Equal(ExplorerAddRoutingVerdict.RejectProcessUnavailable,
            ExplorerAddRoutingFilter.Evaluate(ExplorerAddRoutingFilter.Magic,
                Self with { UserSid = sid, SessionId = session }, Self));
    }
}
