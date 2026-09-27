using TileWall.Core.Entries;
using Xunit;

namespace TileWall.Core.Tests.Entries;

/// <summary>
/// A7/SC-08+F-04：托管路径真目录边界（EntryPathGuard）。字符串前缀 ≠ 边界——
/// 段级结构（恰为 Objects/&lt;Id&gt;/&lt;名&gt;.lnk|.url）+ GetFullPath 目录边界比较；
/// 覆盖 ../ 父段、绝对路径、跨对象、混合分隔符、子目录嵌套、非法字符与真实文件系统规范化。
/// </summary>
public class EntryPathGuardTests
{
    private const string ObjectId = "obj-a";

    // —— 段级结构 + Id 匹配 + 目录边界：正例 ——

    [Theory]
    [InlineData("Objects/obj-a/工作浏览器.lnk")]
    [InlineData("Objects/obj-a/页面.URL")] // 扩展名大小写不敏感
    [InlineData("objects/OBJ-A/页面.url")] // Objects 与 Id 段大小写不敏感（Windows 语义）
    public void 合法对象目录入口_通过(string relativePath)
    {
        Assert.True(EntryPathGuard.IsEntryRelativePathAllowed("tw-root", ObjectId, relativePath));
    }

    // —— 负例：父段 / 绝对路径 / 跨对象 / 混合分隔符 / 嵌套 / 扩展名 ——

    [Theory]
    [InlineData("Objects/obj-a/../../../outside.lnk")] // 父段逃逸
    [InlineData(@"Objects/obj-a\..\..\outside.lnk")]   // 混合分隔符 + 父段逃逸
    [InlineData("Objects/../Objects/obj-a/x.lnk")]     // 中段父段（规范化后虽在界内，父段一律拒绝）
    [InlineData(@"C:\outside.lnk")]                    // 绝对路径
    [InlineData(@"C:\root\Objects/obj-a/x.lnk")]       // 盘符绝对 + 混合分隔符
    [InlineData(@"\Objects\obj-a\x.lnk")]              // 根反斜杠
    [InlineData("Objects/obj-b/浏览器.lnk")]           // 跨对象引用
    [InlineData("Objects/obj-a/sub/x.lnk")]            // 子目录嵌套（入口只能是目录下单文件）
    [InlineData("Objects/obj-a/notes.txt")]            // 扩展名非法
    [InlineData("Objects/obj-a/noext")]                // 无扩展名
    [InlineData("Objects//x.lnk")]                     // 空 Id 段
    [InlineData("Objects/obj-a/x:.lnk")]               // 段内冒号（NTFS 流语法/盘符歧义）
    [InlineData("Objects/obj-a/..")]                   // 文件名段为父段
    [InlineData("")]
    [InlineData("Objects/obj-a/")]
    public void 越界或畸形路径_拒绝(string relativePath)
    {
        Assert.False(EntryPathGuard.IsEntryRelativePathAllowed("tw-root", ObjectId, relativePath));
    }

    // —— 完整路径形态（EntryLauncher 接线，F-04）——

    [Theory]
    [InlineData(@"C:\tw-root\Objects\obj-a\工作浏览器.lnk", true)]
    [InlineData(@"C:\tw-root\Objects\obj-a\页面.url", true)]
    [InlineData(@"C:\tw-root\Objects\obj-b\浏览器.lnk", true)] // launcher 形态只验结构（Id 由完整路径自带）
    [InlineData(@"C:\tw-root\Objects\obj-a\..\..\outside.lnk", false)] // 父段：未规范化
    [InlineData(@"C:/tw-root/Objects/obj-a/x.lnk", false)]             // 混合分隔符：未规范化
    [InlineData(@"C:\tw-root\Staging\c1\x.lnk", false)]                // 非托管对象目录
    [InlineData(@"C:\tw-root\Objects\obj-a\x.txt", false)]             // 扩展名
    [InlineData("Objects/obj-a/x.lnk", false)]                         // 相对路径不收
    public void 托管完整路径判定(string fullPath, bool expected)
    {
        Assert.Equal(expected, EntryPathGuard.IsManagedEntryFullPath(fullPath));
    }

    [Fact]
    public void 真实根目录下_合法路径在界内_逃逸路径被规范化比较拒绝()
    {
        // GetFullPath 真语义自洽：合法路径规范化后仍在 <root>\Objects\obj-a\ 内；
        // 逃逸路径规范化后越出对象目录 → 拒绝
        var root = Path.Combine(Path.GetTempPath(), "tilewall-a7-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Assert.True(EntryPathGuard.IsEntryRelativePathAllowed(root, ObjectId, "Objects/obj-a/工作浏览器.lnk"));
            Assert.False(EntryPathGuard.IsEntryRelativePathAllowed(root, ObjectId, "Objects/obj-a/../../../outside.lnk"));
            Assert.False(EntryPathGuard.IsEntryRelativePathAllowed(root, ObjectId, "Objects/obj-a/..\\..\\outside.lnk"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
