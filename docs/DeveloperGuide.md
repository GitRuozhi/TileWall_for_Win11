# TileWall 开发者指南

TileWall 是一款可编辑的图片磁贴启动墙：把程序、文件夹、文件、网页与应用协议入口放在同一面墙上，用普通磁贴、连续图片拼图和图集表达自己的布置。它使用 C#、.NET 10 与 WinUI 3，独立于 Windows Shell；可选的键盘 Win 键与任务栏开始按钮接管仍是实验功能。

本文面向第一次取得公开源码的开发者，介绍怎样构建、怎样沿一次操作找到实现，以及修改时需要保留的行为。产品使用说明见[中文 README](../README.zh-CN.md)或[英文 README](../README.md)，版本变化见[更新日志](CHANGELOG.md)，数据处理说明见[隐私说明](store/privacy.md)。已发布版本和附件以 [GitHub Releases](https://github.com/GitRuozhi/TileWall_for_Win11/releases) 为准。

## 支持系统与分发方式

正式支持 Windows 10 22H2（build 19045）和 Windows 11，当前发行架构为 x64。工程保留更低的技术安装门槛，允许较旧 Windows 10 尝试运行，但不承诺兼容。项目中的其他平台配置也不代表已有对应架构的发行和实机验证。

用户可从 [Microsoft Store](https://apps.microsoft.com/store/detail/9NZZN35FDB0C) 安装，或从 Releases 取得便携 ZIP、EXE 安装器。ZIP 和 EXE 包含所需的 .NET 与 Windows App SDK 运行时；EXE 安装、更新和卸载需要管理员权限，应用日常以普通权限运行。商店交付的 MSIX 由商店签名，本地生成的未签名 MSIX 不能直接作为普通安装包使用。

同一 Windows 用户的三种分发方式共用 `%LOCALAPPDATA%\TileWall` 数据及单实例。开发构建也使用该目录；程序目录不是个人数据目录。首次运行会建立欢迎墙，已有数据根和用户后来清空的墙不会再次自动播种。开发验证前先退出已运行的 TileWall，避免不同来源的程序竞争同一份数据。

## 从公开源码构建

### 环境和依赖

在 Windows x64 上准备 .NET 10 SDK。使用 Visual Studio 时需要支持该 SDK 的 .NET 桌面／WinUI 构建工具；命令行也可直接调用 `dotnet`。原生组件另需 Visual Studio 或 Build Tools 中的 C++ x64 工具，以及含 x64 工具和库的 Windows SDK；下面的原生脚本使用 PowerShell 7（`pwsh`）。

建议检出到较短的本地路径，例如 `D:\TileWall`。真实 Shell 集成夹具会在测试输出下再创建多层目录，过深的检出／输出路径可能触及原生快捷方式 IO 的路径限制，即使托管编译已经成功。遇到这类失败先保留错误信息并在短路径复验，不通过跳过用例来宣告通过。

NuGet 在首次构建时还原依赖，需要访问配置的软件包源。应用的目标框架、Windows SDK、Windows App SDK 组件、原生资源读取依赖及自包含 .NET 运行时版本，以 [TileWall.csproj](../src/TileWall/TileWall.csproj) 为准；测试依赖见 [TileWall.Core.Tests.csproj](../src/TileWall.Core.Tests/TileWall.Core.Tests.csproj)。无需另建项目、运行 `winapp init` 或修改这些固定版本才能开始。

### 构建可直接运行的开发程序

在公开仓根目录打开 PowerShell，执行：

```powershell
dotnet publish src/TileWall/TileWall.csproj -c Release -r win-x64 --self-contained true --artifacts-path artifacts/.build --output artifacts/Release/TileWall -p:Platform=x64 -p:WindowsPackageType=None
```

命令会还原并编译应用及 Core，生成自包含、非打包的开发输出。中间产物位于 `artifacts/.build/`，程序位于 `artifacts/Release/TileWall/`；保留整个目录，不能只复制 EXE 或混入另一轮构建的 DLL。确认之前的实例已经退出后，可直接运行：

```powershell
.\artifacts\Release\TileWall\TileWall.exe
```

这是普通开发构建，不生成官方 ZIP、EXE 安装器或商店提交包。公开仓不包含维护者的完整发行包装脚本和安装器配置；运行、编译与安装／签名是不同步骤。不要用 `dotnet run` 的自动包身份流程代替这里的直接 EXE 启动来核验非打包行为。

### 运行 Core 测试

```powershell
dotnet test src/TileWall.Core.Tests/TileWall.Core.Tests.csproj -c Release --artifacts-path artifacts/.build/core-tests
```

`dotnet test` 会自动还原并构建测试项目。Core 不依赖 WinUI，但测试集中含有真实 Windows Shell、快捷方式和文件系统集成用例，完整验证应在 Windows 上进行；检查失败与跳过原因，不能把跳过计为通过。

### 单独构建原生组件

需要修改对应系统集成时，可在仓库根目录用 PowerShell 7 执行：

```powershell
pwsh -NoProfile -File tools/BuildExplorerExtension.ps1 -OutputDirectory artifacts/.build/native-shell
pwsh -NoProfile -File tools/BuildUninstallBroker.ps1 -OutputDirectory artifacts/.build/native-uninstall
```

脚本自动定位已安装的 MSVC 和 Windows SDK；输出目录必须位于当前仓库 `artifacts/` 下。分别生成 `TileWall.ExplorerExtension.dll` 和 `TileWall.UninstallBroker.exe`。单独构建不会注册 COM、安装包或执行卸载；Explorer 扩展通过 MSIX 的清单声明使用，卸载辅助程序服务于 EXE 安装器的普通用户清理交接。它们不是平时运行磁贴墙所需的独立启动入口。

## 沿一次磁贴编辑找到代码

以“把已有磁贴改成宽型并更换图片”为例，正式状态沿下列顺序变化：

1. [SharedMemberEditorDialog.cs](../src/TileWall/Views/SharedMemberEditorDialog.cs) 持有当前编辑会话的局部草稿，[MemberEditorPreview.cs](../src/TileWall/Views/MemberEditorPreview.cs) 显示预览。取消不会把草稿、图片或入口写入正式墙。
2. 保存回调进入 [MainWindow.xaml.cs](../src/TileWall/MainWindow.xaml.cs) 的 `SaveSharedMember`，检查实际属性变化及需要重验的启动身份，构造 `ReplaceMemberAction`。成员及其设置的模型定义在 [WallDocument.cs](../src/TileWall.Core/Wall/WallDocument.cs)。
3. `WallSession.Propose` 调用 [WallLayoutEngine.cs](../src/TileWall.Core/Wall/WallLayoutEngine.cs) 计算 `WallProposal`；[WallViewportPolicy.cs](../src/TileWall.Core/Wall/WallViewportPolicy.cs) 处理视口容量。提案只是候选结果，不等于已保存。扩大尺寸时依次尝试原位、原组最近完整空位、组尾及墙尾后备；失败保留原墙和草稿。
4. [WallImageService.cs](../src/TileWall/Graphics/WallImageService.cs) 及相关 partial 准备正式图像，冻结本次确认的字节。主窗口的保存入口把提案、入口变更与准备媒体交给 [WallSession.cs](../src/TileWall.Core/Application/WallSession.cs) 的 `Commit` 或 `CommitAsync`；异步编排入口见 [MainWindow.Persistence.cs](../src/TileWall/MainWindow.Persistence.cs)。
5. [WallStore.cs](../src/TileWall.Core/Storage/WallStore.cs) 重新核对代次、磁盘清单、入口内容及资源，执行联合事务。软件设置、活动墙、回收站、托管入口和新增正式原图不能拆开保存；原子替换 `state.json` 是唯一提交点。
6. Store 成功后，Session 接纳实际已提交的快照，再通知界面刷新。[WallSurface.cs](../src/TileWall/Views/WallSurface.cs) 与 [WallVisualFactory.cs](../src/TileWall/Views/WallVisualFactory.cs) 呈现同一成员语义。磁盘已提交而界面未接纳、写入结果未知、恢复未完成，都有独立的阻断状态，不能通过重画界面当作保存成功。

页面只拥有草稿；`WallSession` 是 UI 进程已提交活动快照的唯一所有者，`WallStore` 负责正式持久化。这一边界同样用于拖动、导入、初始化和后台图像提交。异步结果必须匹配当前成员、来源、请求和代次；关页取消等待，也不代表共享原生工作已经结束。

## 理解几个容易混淆的行为

### 网格、吸附与容量

位置使用分组内的整数格；[WallMetrics.cs](../src/TileWall.Core/Wall/WallMetrics.cs) 换算 DIP，窗口层再处理屏幕物理像素、DPI 和滚动。基础格 48 DIP、缝隙 4 DIP、步距 52 DIP，每栏八格宽 412 DIP，栏缝 16 DIP。分组有独立标题行，不能只计算成员的占用高度。

例如拖动一块 4×2 磁贴时，先按完整宽度吸附左上角，再由同一整数落点决定组、栏和避让；抓住磁贴哪个角不改变最终位置。[WallMemberDropResolver.cs](../src/TileWall.Core/Wall/WallMemberDropResolver.cs) 使用手势开始时的布局，松手消费最后一次输入并提交预览的同一份提案。非法落点、Esc、失捕或保存失败恢复原布局。

可见窗口宽度、逻辑墙容量与派生填充栏分开计算。拓展墙最多 64 栏，包含真实尾空栏；显示填充不写配置，也不占这一额度。容量适配先消除必要空栏／完整空行，再按原组序重排完整分组，仍放不下则整次拒绝。布局锁保留成员和分组占位，单成员移动只进入已有空位，不挤动其他内容；实现见 [WallLayoutEngine.Locked.cs](../src/TileWall.Core/Wall/WallLayoutEngine.Locked.cs)。

### 导入身份、过滤与组内去重

[LaunchIdentityService.cs](../src/TileWall.Core/Entries/LaunchIdentityService.cs) 从目标、参数与工作目录生成启动身份，显示名称不是身份。同一程序用不同参数启动可以是不同入口，同一入口放在不同分组也允许；同一目的组内的重复项会跳过，已有成员保留 ID 和属性。

例如两个名字不同但目标、参数和工作目录相同的快捷方式，不应因为名称不同就在同组出现两份。快捷方式保留原始字节，来源名与真实启动目标分开保存。路径身份采用词法规范化，8.3 短路径与长路径不保证合并；Shell PIDL、协议和普通路径各有实际身份合同。

[ImportSourcePlanner.cs](../src/TileWall.Core/Import/ImportSourcePlanner.cs) 对纯来源先过滤明确丢失目标、再过滤次要入口、再按来源身份去重，之后才识别软件家族／白名单厂商、原目录和字母组。权限不足或暂不可达不等于目标丢失。墙包是带属性来源，保留记录的几何和外观。来源缓存只用于首显，确认添加前仍重新读取来源、目的组入口和当前墙，再整批规划与提交；取消不部分保存。

### 自动图标系列与选帧

自动图标区分“选择哪一个来源系列”和“从该系列显示哪一帧”。[WallImageService.AutomaticSeries.cs](../src/TileWall/Graphics/WallImageService.AutomaticSeries.cs) 按真实入口允许的来源顺序采集、校验和选择系列；用户显式图片优先，不按程序名或参数猜另一个程序，也不把不同来源的帧混成一个系列。确认后全部不同的有效原生分辨率进入正式 `Media`。

假设同一系列有 32、64、256 像素帧，本次最终物理需求为 100×100，就选择 256 帧；需求为 64×64 时选 64 帧；都不足时用最大帧。[AutomaticIconSeriesRules.cs](../src/TileWall.Core/Wall/AutomaticIconSeriesRules.cs) 与 [ImageDecodeSizing.cs](../src/TileWall.Core/Imaging/ImageDecodeSizing.cs) 结合契合度、倍率和 DPI 计算需求。已保存的 `SelectedFrame` 仍是稳定几何基准，换显示帧不会换来源或改变原始模式的像素含义。

已绑定系列重开后从正式原图读取，不因缓存淘汰或原来源失效静默重采。HTTP(S) 网站使用明确域名匹配的随包离线品牌图，不下载网页 favicon。图标读取和图片解码仍保留格式、真实表示范围、透明像素与实际分配检查；可淘汰缓存不能限制或代替正式原图。

### 拼图是一张连续画布

完整拼图是一名成员，各矩形分区裁切同一张图，共享标题、外观及一个可选目标。把 4×4 拼图分成四块 2×2，只改变分区，不会生成四个可分别启动的磁贴。[PuzzleSceneGeometry.cs](../src/TileWall/Graphics/PuzzleSceneGeometry.cs) 和 [PuzzleCanvasView.cs](../src/TileWall/Views/PuzzleCanvasView.cs) 处理连续画布与显示，公共图像变换见 [MemberImageGeometry.cs](../src/TileWall.Core/Imaging/MemberImageGeometry.cs)。编辑页的分割、拼合和尺寸草稿支持会话内撤销／重做，正式墙不提供操作历史。

### 图集轮换与来源变化

新建磁贴和拼图默认每 5 秒轮换；已有对象与历史缺字段读取保留原兼容规则。图集洗牌，一轮用完前不重复，跨轮不相邻重复。外部 `Folder` 读取真实目录的顶层最多 99 张可解码图片；受管图库读取正式 `Sources` 清单，二者不能混同。

[PuzzlePresentationCoordinator.cs](../src/TileWall/Application/PuzzlePresentationCoordinator.cs) 管理当前帧、轮换和正式结果接纳；[WallImageService.Gallery.cs](../src/TileWall/Graphics/WallImageService.Gallery.cs) 合并同来源的约 60 秒检查。隐藏、拖动、编辑或阻断模态时暂停轮播，重新显示到期只切一次；新帧准备并持久提交成功才替换旧画面。确证目录不存在时显示空图并保留路径，无法确认的权限／IO 故障保留最近有效缓存及诊断。不要为了更快刷新而跳过来源复核或另建持续目录监听。

### 墙包交换与兼容

[Exchange](../src/TileWall.Core/Exchange/) 以标准 ZIP 分享所选活动成员，包括组／栏序、几何、分区、托管入口和所需原图；不包含软件设置、回收站、外部程序／文档内容或缓存。外部 `Folder` 保留目录路径，受管图库携带完整清单和原图。自动图标携带已确认的完整系列，接收端从包内资源选显示帧，不按包外启动目标重取图标。

当前写 format 6／wall schema 6，Reader 兼容 format 2–5，并继续接受旧 `.twpack` 扩展名。导入先校验整包的路径、摘要、资源闭包、身份与几何，再展示候选；选择性导入沿原联合事务，不自动替换整墙或恢复设置。已正式交付的墙包格式是新版读取的兼容责任，未来格式提示升级，不部分导入或强行降级。实现入口为 [WallPackageReader.cs](../src/TileWall.Core/Exchange/WallPackageReader.cs)、[WallPackageSnapshot.cs](../src/TileWall.Core/Exchange/WallPackageSnapshot.cs) 和 [WallPackageWriter.cs](../src/TileWall.Core/Exchange/WallPackageWriter.cs)，历史样本见 [Compatibility](../src/TileWall.Core.Tests/Fixtures/Compatibility/)。

## 窗口、Shell 与数据职责

| 要找的实现 | 入口与边界 |
| --- | --- |
| 启动、退出、主实例及外部添加队列 | [Program.cs](../src/TileWall/Program.cs)、[WallApplicationLifetime.cs](../src/TileWall/Application/WallApplicationLifetime.cs)、[SingleInstanceGate.cs](../src/TileWall/Shell/Interop/SingleInstanceGate.cs)。关闭要排空已接受的保存、页面与后台任务，再释放系统资源及单实例所有权。 |
| 主墙、拖动、缩放与设置保存 | [MainWindow.xaml.cs](../src/TileWall/MainWindow.xaml.cs) 及同目录 partial、[WallSurface.cs](../src/TileWall/Views/WallSurface.cs)、[SettingsDialog.cs](../src/TileWall/Views/SettingsDialog.cs)。窗口几何不等于正式网格坐标。 |
| 编辑、设置和导入弹窗 | [OwnedDialogWindow.cs](../src/TileWall/Views/OwnedDialogWindow.cs)、[SharedMemberEditorDialog.cs](../src/TileWall/Views/SharedMemberEditorDialog.cs)、[ImportDialog.cs](../src/TileWall/Views/ImportDialog.cs)。模态期间保护主墙并保留草稿，关闭等待已接受的任务完成。 |
| 图片、图标、背景与缓存 | [Graphics](../src/TileWall/Graphics/)、[WallBackdrop.cs](../src/TileWall/Views/WallBackdrop.cs)。Acrylic 在主墙仍隐藏时，于呼出或直接显示所属弹窗前经唯一入口准备底图；主墙与弹窗共享当次底图。墙可见期间不重新取景，也不为取景改变焦点或窗口视觉状态，底图只存内存。 |
| Explorer 添加及普通启动 | [ExplorerContextMenuController.cs](../src/TileWall/Shell/Interop/ExplorerContextMenuController.cs)、[ExplorerAddChannel.cs](../src/TileWall/Shell/Interop/ExplorerAddChannel.cs)、[EntryLauncher.cs](../src/TileWall.Core/Shell/EntryLauncher.cs)。ZIP／EXE 菜单只改当前用户 HKCU；目标启动交给 Windows Shell，导入和预览不执行目标。 |
| MSIX Explorer 菜单 DLL | [TileWall.ExplorerExtension](../src/TileWall.ExplorerExtension/)。原生 COM 菜单取得文件系统路径并激活应用，应用再走单实例与正式添加流程；DLL 不直接保存墙。 |
| EXE 卸载辅助程序 | [TileWall.UninstallBroker](../src/TileWall.UninstallBroker/)。原生普通用户 broker 认证交接并清理精确属于本安装的个人注册，安装器负责程序文件；不以提升账户的 HKCU 替代原用户，也不删除个人墙数据。 |
| 中英文界面文案 | [Resources](../src/TileWall/Resources/)。用户自己的标题、路径和入口名不翻译。 |

[RuntimeScope.cs](../src/TileWall/Application/RuntimeScope.cs) 解析物理用户数据根及运行作用域。[存储实现](../src/TileWall.Core/Storage/) 以数据根的 `state.json` 清单提交统一代次，指向设置、墙和回收站三份不可变 DTO；`Objects` 存托管入口，`Media` 存单图、背景和原生系列，`Sources` 存受管图库，`Snapshots`、`Staging` 与 `Recovery` 保存快照和事务恢复材料。`Cache` 是可重建缓存，`logs` 是本机日志。

保存和恢复按实际字节摘要核验，不跨代拼接；每次实际 IO 检查真实祖先和 reparse 边界。新版只自动升级可识别旧格式，未知较高格式阻断并保留现场。不要通过删除恢复材料、跳过入口重读或把正式媒体视为缓存来“修好”打开失败。手动备份／恢复需要退出应用并处理完整数据根；分享 ZIP 不能代替它。

外部快捷方式、程序、图片与文档只读，不向原件写回。应用自有代码没有后台版本检查、云同步或自动诊断上传，但 Shell、UNC／映射盘、同步文件提供者及随包 SDK 可能触发各自的网络与诊断行为，不能把本地工作方式解释为绝对零网络。

## 怎样验证和反馈

先按改动选择验证：模型、布局、身份与事务可以从 [Core 测试](../src/TileWall.Core.Tests/) 和既有兼容样本入手；图片解码、窗口、真实输入、Shell／COM、安装与保存重启仍需 Windows 实际操作。构建成功和 Core 全过不证明 GUI、安装升级、签名或系统／DPI／语言／账户矩阵通过。

例如修改磁贴编辑，既检查保存后的属性，也检查取消与保存失败保留原数据；修改导入，要覆盖成功、目的组重复、来源变化与取消；修改退出，要验证有未完成任务时仍能正确结算。测试应验证可观察合同，不能删掉正式校验来让模拟输入通过。共享桌面与实例串行操作，使用可丢弃样本，并记录本次真正覆盖的环境和分支。

在 [Discussions](https://github.com/GitRuozhi/TileWall_for_Win11/discussions) 提交问题、建议或贡献意向。反馈时提供版本／来源提交、分发方式、Windows build、显示比例、明确步骤、预期与实际结果、能否稳定复现，以及相关日志或小样本。日志和墙包可能含路径、网址、用户名及入口参数，分享前自行检查并去除敏感内容。

目前由维护者单人开发。贡献前先说明问题与拟改范围，涉及交互、布局、兼容或数据格式的取舍先沟通；补丁保持集中，附针对性的验证结果和未覆盖项，保留来源、许可及历史兼容样本。公开仓现有的文件和入口就是本文命令与链接的依据，不要求取得内部协作文件才能构建或参与讨论。

## 许可证与素材来源

TileWall 项目代码采用根目录 [MIT License](../LICENSE)，版权署名为 `Copyright (c) 2026 Ruozhi`。再分发时保留许可证要求的版权与许可声明。

第三方软件和素材保留各自声明及许可，根 MIT 不替代它们的许可或品牌权利。主要入口是 [THIRD-PARTY-NOTICES.txt](../src/TileWall/THIRD-PARTY-NOTICES.txt) 和 [Licenses](../src/TileWall/Licenses/)，其中记录 ReStart 任务栏实现的改编、AsmResolver、.NET、Windows App SDK、Windows SDK 与 WebView2 等依赖。

随包网站图标的逐文件来源、固定修订、摘要和许可记录见 [catalog.json](../src/TileWall/Assets/FallbackIcons/Websites/catalog.json)、[NOTICE.txt](../src/TileWall/Assets/FallbackIcons/Websites/NOTICE.txt) 及同目录 `SOURCE-LICENSE`／`SOURCE-NOTICE` 文件。第一方系统与通用备用图的来源记录分别见 [System/assets.json](../src/TileWall/Assets/FallbackIcons/System/assets.json) 和 [Generic/assets.json](../src/TileWall/Assets/FallbackIcons/Generic/assets.json)。复用代码或更换素材时，一并核对和保留对应来源与声明。
