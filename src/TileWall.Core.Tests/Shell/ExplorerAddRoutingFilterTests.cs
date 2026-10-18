using Xunit;

using TileWall.Core.Shell;

namespace TileWall.Core.Tests.Shell;

/// <summary>WM_COPYDATA 声明 HWND 映像路由过滤——魔数、映像路径一致性、综合结果。</summary>
public sealed class ExplorerAddRoutingFilterTests
{
    private const string SelfPath = @"C:\Apps\TileWall\TileWall.exe";

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
            ExplorerAddRoutingFilter.Evaluate(0, SelfPath, SelfPath)); // 即便映像完全一致也先拒魔数
        Assert.Equal(
            ExplorerAddRoutingVerdict.RejectBadMagic,
            ExplorerAddRoutingFilter.Evaluate(uint.MaxValue, SelfPath, SelfPath));
    }

    [Fact]
    public void 同一映像路径_通过路由过滤()
    {
        Assert.Equal(ExplorerAddRoutingVerdict.Accept, ExplorerAddRoutingFilter.Evaluate(ExplorerAddRoutingFilter.Magic, SelfPath, SelfPath));
    }

    [Fact]
    public void 映像比对_大小写不敏感()
    {
        Assert.True(ExplorerAddRoutingFilter.DeclaredWindowImageMatches(@"c:\apps\tilewall\tilewall.exe", @"C:\APPS\TILEWALL\TILEWALL.EXE"));
        Assert.Equal(
            ExplorerAddRoutingVerdict.Accept,
            ExplorerAddRoutingFilter.Evaluate(ExplorerAddRoutingFilter.Magic, @"c:\apps\tilewall\tilewall.exe", SelfPath));
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
            ExplorerAddRoutingFilter.Evaluate(ExplorerAddRoutingFilter.Magic, @"D:\OtherCopy\TileWall.exe", SelfPath));
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
            ExplorerAddRoutingVerdict.RejectImagePathMismatch,
            ExplorerAddRoutingFilter.Evaluate(ExplorerAddRoutingFilter.Magic, null, SelfPath));
        Assert.Equal(
            ExplorerAddRoutingVerdict.RejectSelfImageUnavailable,
            ExplorerAddRoutingFilter.Evaluate(ExplorerAddRoutingFilter.Magic, SelfPath, null)); // 本进程路径不可得：无法进行路由过滤
    }
}
