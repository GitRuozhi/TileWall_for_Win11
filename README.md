# TileWall

TileWall 是面向 Windows 10 22H2 和 Windows 11 的独立磁贴启动墙，以 Windows 10 开始菜单为视觉与交互参考。系统开始菜单与 Win 键照常工作。

当前源码版本 **0.2.8**（包版本 **0.2.8.0**）。拖拽性能与流畅度专项仍暂停，Windows 11 系统、多 DPI 和正式签名安装尚未完成实测。

## 功能

- 用磁贴和分组组织程序、文件、文件夹、快捷方式及网址；保留快捷方式的参数和工作目录。
- 普通磁贴、共享图片的拼图、时间与日期组件；支持图片、图集、随机轮换和拼图分区错峰切图，图集可见时约60秒检查来源变化。
- 分组标题在网格预留48 DIP一行，文字和实际交互位于底部30 DIP；支持拖动分组、双击改名、成员拖放和撤销。
- 主题可跟随系统或选择亮色、暗色。墙面与共享磁贴区域支持 Windows 10 方角、Windows 11 圆角；页面外壳使用固定样式，动作图标使用 Segoe MDL2 Assets，拼图预设使用对应分区示意图。
- Acrylic、Mica 或本地图片背景；默认背景透明度 10%，背景网格默认关闭，开启时按明暗主题显示 5% 不透明的格子。Windows 10 的 Mica 当前使用纯色回退。
- 四角锚点独立于全屏；全屏边栏与菜单列贴侧边并占满屏高，磁贴上下和远侧留5%。窗口宽高最多为主屏90%，两种模式均可开启拓展并横向滚动。支持托盘、Win+~ 默认快捷键、登录后启动、常驻显示和置顶设置。
- 从开始菜单导入，支持自动过滤、识别分组、按组名增量合并及组内目标去重，默认保留已有改动；可注册 Windows 10 的 .lnk 右键“添加到 TileWall”。
- 中英文界面；三卡片编辑页提供 1:1 预览、目标自动识别、外观属性与尺寸调整。普通磁贴最多 4×4，拼图整体最多 8×12，每个分区最多 4×4。

## 构建

需要 Windows、.NET SDK 10 和相应的 Windows SDK / Visual Studio C++ 构建工具。Windows App SDK 2.5.1 由 NuGet 还原。项目入口：[TileWall.csproj](src/TileWall/TileWall.csproj)。

```powershell
dotnet publish src/TileWall/TileWall.csproj -c Release -r win-x64 --self-contained true -p:Platform=x64 -p:WindowsPackageType=None -o artifacts/Release/TileWall
```

从发布目录运行 `TileWall.exe`；不要直接运行缺少发布资源的裸编译目录。私有开发仓库另提供 `tools/build.ps1`、`tools/clean.ps1` 及完整开发与验收文档。

Core 测试：

```powershell
dotnet test src/TileWall.Core.Tests
```

## 数据与发行

便携运行数据位于 `%LOCALAPPDATA%\TileWall`；打包运行使用应用自己的 LocalFolder，两者不互读。完整手动备份请先退出软件再复制对应数据根。外部程序、原始快捷方式和图片不会被作为应用数据删除。

当前数据格式为Schema4，只支持当前格式，不自动重置或迁移旧数据。“取消固定”保留非激活记录和托管入口，可用当前撤销点撤回。单图和自定义墙背景随保存托管原图，保存后外部原图移动或删除不影响显示；图集继续跟随真实文件夹。

源码仓库：[GitRuozhi/TileWall_for_Win11](https://github.com/GitRuozhi/TileWall_for_Win11)。发行文件以 [Releases](https://github.com/GitRuozhi/TileWall_for_Win11/releases) 实际内容为准，源码版本更新不代表已完成正式发行验收。

## 许可

许可方向是源码公开、免费使用（包括企业内部办公），限制未经授权的商业化分发及商业衍生。这不是 OSI 意义上的开源许可；正式授权文本尚未发布，发布前保留所有权利。第三方组件说明见 [THIRD-PARTY-NOTICES](src/TileWall/THIRD-PARTY-NOTICES.txt)。
