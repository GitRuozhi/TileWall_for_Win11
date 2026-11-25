# TileWall Developer Guide

**English** · [简体中文](DeveloperGuide.zh-CN.md)

TileWall is an editable tile launcher for programs, folders, files, websites, and application protocols. You can arrange ordinary tiles, continuous-image puzzles, and image albums on the same wall. It uses C#, .NET 10, and WinUI 3 and runs independently of Windows Shell. The optional Windows-key and taskbar Start-button integration is experimental.

This guide helps developers build the public source, follow a user action through the code, and understand the behavior their changes need to preserve. Product instructions are in the [English README](../README.md) and [Chinese README](../README.zh-CN.md). Version changes are recorded in the [changelog, in Chinese](CHANGELOG.md). Published releases and downloads are listed on [GitHub Releases](https://github.com/GitRuozhi/TileWall/releases).

- To run the app, start with [getting the source and building](#build-from-public-source).
- To find an implementation, follow [a tile edit through the code](#follow-a-tile-edit-through-the-code), then use the [responsibility index](#window-shell-and-data-responsibilities).
- Before changing code, read the relevant [behavior notes](#behavior-to-understand-before-making-changes) and [validation guidance](#validate-changes-and-report-problems).
- To help maintain TileWall, you are welcome to [join the development team](#join-the-development-team).

## Supported systems and distribution

TileWall officially supports Windows 10 22H2, build 19045, and Windows 11, with native x64 and ARM64 builds. The technical installation minimum allows attempts on older Windows 10 versions, whose compatibility is unconfirmed. Build and package checks cover only part of platform validation. Native ARM64 execution and installation have yet to be tested on hardware.

Users can install from the [Microsoft Store](https://apps.microsoft.com/store/detail/9NZZN35FDB0C) or download a portable ZIP or EXE installer from Releases. ZIP and EXE distributions include the required .NET and Windows App SDK runtimes. EXE installation, updates, and uninstallation require administrator privileges, while the app runs with ordinary user privileges. The Store signs its MSIX distribution. Locally generated unsigned MSIX packages require a separate signing and deployment setup.

All three distributions share `%LOCALAPPDATA%\TileWall` and single-instance ownership for the current Windows user. Development builds use the same data directory. The first run creates a welcome wall. Existing data roots, including walls the user has emptied, retain their contents without being seeded again. Exit any running TileWall instance before development validation so that builds from different sources use the data in turn.

## Build from public source

### Environment and dependencies

On Windows x64, install Git, the .NET 10 SDK, and PowerShell 7, available as `pwsh`. Visual Studio users need .NET desktop and WinUI build tools that support this SDK. The command-line build uses `dotnet`. Native components also need the C++ x64 tools and corresponding Windows SDK from Visual Studio or Build Tools. To cross-compile for ARM64, also install `Microsoft.VisualStudio.Component.VC.Tools.ARM64` and the SDK's ARM64 libraries.

Use a short local checkout path, such as `D:\TileWall`. Real Shell integration fixtures create several nested directories under the test output. Deep checkout or output paths can reach native shortcut IO limits even when managed compilation succeeds. If that happens, retain the error details and retry from a shorter path. Record skipped cases separately from passing results.

The first build restores dependencies from the configured NuGet feeds and needs access to them. [TileWall.csproj](../src/TileWall/TileWall.csproj) defines the target framework, Windows SDK, Windows App SDK components, native resource-reading dependencies, and self-contained .NET runtime version. Test dependencies are in [TileWall.Core.Tests.csproj](../src/TileWall.Core.Tests/TileWall.Core.Tests.csproj). Build the existing projects with their pinned versions.

### Get the source

Open PowerShell in the directory where you want to keep the project. Clone the public repository and enter its root. Run subsequent commands from this directory.

```powershell
git clone https://github.com/GitRuozhi/TileWall.git
Set-Location TileWall
```

### Build and run the development app

Run this command from the repository root.

```powershell
dotnet publish src/TileWall/TileWall.csproj -c Release -r win-x64 --self-contained true --artifacts-path artifacts/.build --output artifacts/Release/TileWall -p:Platform=x64 -p:WindowsPackageType=None
```

It restores and compiles the app and Core into a self-contained, unpackaged development output. Intermediate files go to `artifacts/.build/`, and the runnable app goes to `artifacts/Release/TileWall/`. Keep the output directory together, with the EXE and DLLs from the same build. After exiting any previous instance, launch the app directly.

```powershell
.\artifacts\Release\TileWall\TileWall.exe
```

Official ZIP, EXE installer, and Store packages use the maintainer's release tooling and installer configuration, which are maintained in the private repository. For unpackaged development validation, use the direct EXE launch above. The automatic package-identity flow in `dotnet run` exercises a different startup path.

For ARM64 cross-compilation, change the RID to `win-arm64`, `Platform` to `ARM64`, and the output directory to `artifacts/.build/arm64-publish`. Keep the x64 output available for everyday use on the x64 host. Native build scripts use `-Architecture arm64` for the same target and default to x64. Both architectures share the production identity, data root, and single-instance ownership. Run ARM64 output on a matching system.

### Run Core tests

Choose tests that cover the change. Use `--filter` to run relevant cases. For example, the following command selects wall-package reading and writing tests.

```powershell
dotnet test src/TileWall.Core.Tests/TileWall.Core.Tests.csproj -c Release --artifacts-path artifacts/.build/core-tests --filter FullyQualifiedName~TileWall.Core.Tests.Exchange.WallPackageTests
```

To run the full Core test suite when needed, omit the filter.

```powershell
dotnet test src/TileWall.Core.Tests/TileWall.Core.Tests.csproj -c Release --artifacts-path artifacts/.build/core-tests
```

`dotnet test` restores and builds the test project automatically. Core has no WinUI dependency, but its test project includes real Windows Shell, shortcut, and filesystem integration cases. Run complete validation on Windows and report failures and skipped cases accurately.

### Build native components separately

When changing the corresponding system integration, run these scripts from the repository root using PowerShell 7.

```powershell
pwsh -NoProfile -File tools/BuildExplorerExtension.ps1 -OutputDirectory artifacts/.build/native-shell
pwsh -NoProfile -File tools/BuildUninstallBroker.ps1 -OutputDirectory artifacts/.build/native-uninstall
```

The scripts locate installed MSVC and Windows SDK tools. Output directories must be under the current repository's `artifacts/` directory. They produce `TileWall.ExplorerExtension.dll` and `TileWall.UninstallBroker.exe`. COM registration, package installation, and uninstallation are separate deployment operations. The Explorer extension is declared in the MSIX manifest. The uninstall broker handles cleanup handed off to the ordinary user by the EXE installer. Launch the tile wall through `TileWall.exe`.

## Follow a tile edit through the code

Consider changing an existing tile to a wide tile and replacing its image. Committed state moves through these steps.

1. [SharedMemberEditorDialog.cs](../src/TileWall/Views/SharedMemberEditorDialog.cs) owns a local draft for the editing session. [MemberEditorPreview.cs](../src/TileWall/Views/MemberEditorPreview.cs) renders its preview. Canceling discards the draft, images, and entry changes before they reach the committed wall.
2. The save callback reaches `SaveSharedMember` in [MainWindow.xaml.cs](../src/TileWall/MainWindow.xaml.cs). It checks actual property changes and launch identities requiring revalidation, then creates a `ReplaceMemberAction`. [WallDocument.cs](../src/TileWall.Core/Wall/WallDocument.cs) defines the member and its settings.
3. `WallSession.Propose` calls [WallLayoutEngine.cs](../src/TileWall.Core/Wall/WallLayoutEngine.cs) to calculate a `WallProposal`. [WallViewportPolicy.cs](../src/TileWall.Core/Wall/WallViewportPolicy.cs) handles viewport capacity. The proposal is a candidate awaiting persistence. When enlarging a member, placement tries its current position, the nearest complete space in the same group, the group tail, and finally the wall tail. Failure preserves the previous wall and draft.
4. [WallImageService.cs](../src/TileWall/Graphics/WallImageService.cs) and its partial files prepare committed images and freeze the bytes accepted by this confirmation. The main window passes the proposal, entry changes, and prepared media to `Commit` or `CommitAsync` in [WallSession.cs](../src/TileWall.Core/Application/WallSession.cs). Asynchronous orchestration is in [MainWindow.Persistence.cs](../src/TileWall/MainWindow.Persistence.cs).
5. [WallStore.cs](../src/TileWall.Core/Storage/WallStore.cs) rechecks the generation, disk manifest, entry contents, and resources before executing a joint transaction. App settings, the active wall, the Recycle Bin, managed entries, and new committed source images are saved together. Atomic replacement of `state.json` is the sole commit point.
6. After Store succeeds, Session accepts the snapshot actually committed and notifies the UI. [WallSurface.cs](../src/TileWall/Views/WallSurface.cs) and [WallVisualFactory.cs](../src/TileWall/Views/WallVisualFactory.cs) render the same member semantics. A disk commit awaiting UI acceptance, an unknown write outcome, and incomplete recovery each have distinct blocking states. The UI must resolve these states before reporting save success.

Pages own drafts. `WallSession` is the sole owner of the UI process's committed active snapshot, and `WallStore` owns persistence. The same boundary applies to dragging, importing, initialization, and background image commits. Asynchronous results must match the current member, source, request, and generation. Canceling a page's wait can leave shared native work running until it finishes.

## Behavior to understand before making changes

### Grid, snapping, and capacity

Positions use integer grid coordinates within a group. [WallMetrics.cs](../src/TileWall.Core/Wall/WallMetrics.cs) converts them to DIPs, while the window layer handles physical pixels, DPI, and scrolling. A base cell is 48 DIPs with a 4-DIP gap and a 52-DIP pitch. Each eight-cell column is 412 DIPs wide, with a 16-DIP column gap. Groups also have their own title rows, which contribute to layout height.

For a 4×2 tile drag, snapping uses the complete width and top-left position. That integer position determines the group, column, and collision resolution regardless of where the tile was grabbed. [WallMemberDropResolver.cs](../src/TileWall.Core/Wall/WallMemberDropResolver.cs) uses the layout captured when the gesture started. Releasing consumes the last input and commits the same proposal shown by the preview. Invalid drops, Esc, capture loss, and save failures restore the original layout.

Visible window width, logical wall capacity, and derived filler columns are calculated separately. The extended wall supports up to 64 columns, including genuine trailing empty columns. Display fillers are derived without being saved or counted toward that limit. Capacity adaptation first removes the necessary empty columns or complete empty rows, then rearranges whole groups in their original order. If they still do not fit, the entire operation is rejected. Layout lock preserves member and group slots. Moving one member uses existing space and leaves other content in place. See [WallLayoutEngine.Locked.cs](../src/TileWall.Core/Wall/WallLayoutEngine.Locked.cs).

### Import identity, filtering, and duplicates within a group

[LaunchIdentityService.cs](../src/TileWall.Core/Entries/LaunchIdentityService.cs) derives launch identity from the target, arguments, and working directory. Display names are separate. Different arguments can make two launches of the same program distinct. The same entry may appear in different groups. Duplicates within the destination group are skipped, preserving existing members' IDs and properties.

For example, two differently named shortcuts with identical targets, arguments, and working directories represent the same entry within a group. Shortcuts retain their original bytes, with the source name stored separately from the actual launch target. Path identity uses lexical normalization, so 8.3 short paths and long paths may remain distinct. Shell PIDLs, protocols, and ordinary paths each follow their own identity rules.

For plain sources, [ImportSourcePlanner.cs](../src/TileWall.Core/Import/ImportSourcePlanner.cs) filters confirmed missing targets, filters secondary entries, and deduplicates source identities before identifying software families, allowlisted vendors, original directories, and alphabetical groups. Permission failures and temporary inaccessibility leave the target's existence uncertain. Wall packages carry attributes and preserve their recorded geometry and appearance. Source caches support the initial display. Before adding, the app rereads sources, destination entries, and the current wall, then plans and commits the batch together. Canceling leaves the committed state intact.

### Automatic icon series and frame selection

Automatic icons have two decisions, choosing a source series and choosing a display frame from that series. [WallImageService.AutomaticSeries.cs](../src/TileWall/Graphics/WallImageService.AutomaticSeries.cs) collects, validates, and selects series in the source order permitted by the actual entry. Explicit user images take priority. Program names and arguments cannot substitute another program's identity, and frames retain their source-series boundaries. After confirmation, every distinct valid native resolution in the series enters committed `Media`.

If a series has 32-, 64-, and 256-pixel frames, a final physical requirement of 100×100 selects the 256-pixel frame, while 64×64 selects the 64-pixel frame. If every frame is too small, the largest is used. [AutomaticIconSeriesRules.cs](../src/TileWall.Core/Wall/AutomaticIconSeriesRules.cs) and [ImageDecodeSizing.cs](../src/TileWall.Core/Imaging/ImageDecodeSizing.cs) calculate demand from fit, scaling, and DPI. The saved `SelectedFrame` remains the stable geometry reference, so display-frame changes preserve the source and the pixel meaning of Original mode.

On reopening, bound series are read from committed source images. Cache eviction or source loss preserves that binding. HTTP(S) websites use bundled offline brand icons matched to explicit domains. Automatic website icons are supplied locally. Icon reading and image decoding retain format, representation-range, transparent-pixel, and actual-allocation checks. Evictable caches remain separate from committed source images.

### A puzzle is one continuous canvas

A complete puzzle is one member. Its rectangular partitions crop the same image and share a title, appearance, and optional launch target. Dividing a 4×4 puzzle into four 2×2 pieces changes its partitions while preserving the single member and launch target. [PuzzleSceneGeometry.cs](../src/TileWall/Graphics/PuzzleSceneGeometry.cs) and [PuzzleCanvasView.cs](../src/TileWall/Views/PuzzleCanvasView.cs) handle the continuous canvas and display. Shared image transforms are in [MemberImageGeometry.cs](../src/TileWall.Core/Imaging/MemberImageGeometry.cs). Split, merge, and size drafts support undo and redo within the editor session. That history ends with the session.

### Album rotation and source changes

New tiles and puzzles default to rotating every five seconds. Existing objects and historical missing fields retain their compatibility behavior. Albums shuffle without repeating an image within a cycle or showing the same image on both sides of a cycle boundary. An external `Folder` reads up to 99 decodable images from the real directory's top level. A managed album reads the committed `Sources` manifest.

[PuzzlePresentationCoordinator.cs](../src/TileWall/Application/PuzzlePresentationCoordinator.cs) manages the current frame, rotation, and acceptance of committed results. [WallImageService.Gallery.cs](../src/TileWall/Graphics/WallImageService.Gallery.cs) coalesces checks of the same source at roughly 60-second intervals. Rotation pauses while hidden, dragging, editing, or blocked by a modal window. Showing an overdue album advances it once. A new frame replaces the old one after preparation and a successful persistent commit. A confirmed missing directory displays an empty image and retains the path. Uncertain permission or IO failures retain the last valid cache and diagnostics. Source revalidation and the existing polling approach are part of this behavior.

### Wall-package exchange and compatibility

[Exchange](../src/TileWall.Core/Exchange/) shares selected active members in a standard ZIP, including group and column order, geometry, partitions, managed entries, and required source images. App settings, the Recycle Bin, external program and document contents, and caches stay outside the package. External `Folder` sources retain their directory paths. Managed albums include their full manifests and source images. Automatic icons include the complete confirmed series, and the receiver selects display frames from those packaged resources.

The current writer uses format 6 and wall schema 6. The reader supports formats 2–5 and the legacy `.twpack` extension. Before presenting candidates, import validates paths, digests, resource closure, identities, and geometry across the whole package. Selective import uses the joint transaction while preserving the rest of the wall and app settings. New versions retain read compatibility with officially delivered wall-package formats. Future formats produce an upgrade prompt and leave committed state intact. Start with [WallPackageReader.cs](../src/TileWall.Core/Exchange/WallPackageReader.cs), [WallPackageSnapshot.cs](../src/TileWall.Core/Exchange/WallPackageSnapshot.cs), and [WallPackageWriter.cs](../src/TileWall.Core/Exchange/WallPackageWriter.cs). Historical samples are in [Compatibility](../src/TileWall.Core.Tests/Fixtures/Compatibility/).

## Window, Shell, and data responsibilities

| Implementation area | Entry points and boundaries |
| --- | --- |
| Startup, exit, primary instance, external add queue | [Program.cs](../src/TileWall/Program.cs), [WallApplicationLifetime.cs](../src/TileWall/Application/WallApplicationLifetime.cs), and [SingleInstanceGate.cs](../src/TileWall/Shell/Interop/SingleInstanceGate.cs). Shutdown drains accepted saves, page work, and background tasks before releasing system resources and single-instance ownership. |
| Wall, dragging, resizing, and settings saves | [MainWindow.xaml.cs](../src/TileWall/MainWindow.xaml.cs) and its partial files, [WallSurface.cs](../src/TileWall/Views/WallSurface.cs), and [SettingsDialog.cs](../src/TileWall/Views/SettingsDialog.cs). Window geometry and committed grid coordinates use different coordinate spaces. |
| Editor, settings, and import windows | [OwnedDialogWindow.cs](../src/TileWall/Views/OwnedDialogWindow.cs), [SharedMemberEditorDialog.cs](../src/TileWall/Views/SharedMemberEditorDialog.cs), and [ImportDialog.cs](../src/TileWall/Views/ImportDialog.cs). Modal windows protect the main wall and retain drafts. Closing waits for accepted work. |
| Images, icons, backgrounds, and caches | [Graphics](../src/TileWall/Graphics/) and [WallBackdrop.cs](../src/TileWall/Views/WallBackdrop.cs). While the main wall is hidden, a single entry point prepares the Acrylic background before showing the wall or an owned dialog. The wall and dialogs share that capture. The visible wall retains its capture. Capturing preserves focus and window visual state, and the background stays in memory. |
| Explorer add and ordinary launching | [ExplorerContextMenuController.cs](../src/TileWall/Shell/Interop/ExplorerContextMenuController.cs), [ExplorerAddChannel.cs](../src/TileWall/Shell/Interop/ExplorerAddChannel.cs), and [EntryLauncher.cs](../src/TileWall.Core/Shell/EntryLauncher.cs). ZIP and EXE menus modify only the current user's HKCU. Launching is delegated to Windows Shell. Import and preview read targets without executing them. |
| MSIX Explorer menu DLL | [TileWall.ExplorerExtension](../src/TileWall.ExplorerExtension/). The native COM menu obtains filesystem paths and activates the app, which uses the single-instance and committed add flow. Wall persistence stays in the app. |
| EXE uninstall broker | [TileWall.UninstallBroker](../src/TileWall.UninstallBroker/). The native ordinary-user broker authenticates the handoff and cleans up user registrations belonging to this installation. The installer handles program files. Cleanup uses the original user's registrations and preserves personal wall data. |
| Eight-language UI text and language selection | [Resources](../src/TileWall/Resources/) and [WallLanguages.cs](../src/TileWall.Core/Settings/WallLanguages.cs). Missing keys fall back to English. User titles, paths, and entry names retain their original text. |

The app supports English, Simplified Chinese, Traditional Chinese, Japanese, Korean, German, French, Spanish, and following the system language. App-owned strings are in `Resources/ProductStrings*.xaml`. The settings language enum preserves existing values. The six added languages require root-manifest and settings format 6, with trusted older data upgraded in a joint transaction when opened. Share packages and the active wall remain at schema 6. Writing newer data back to an older format is unsupported. Language-resource changes must preserve existing data readability.

[RuntimeScope.cs](../src/TileWall/Application/RuntimeScope.cs) resolves the physical user data root and runtime scope. [Storage](../src/TileWall.Core/Storage/) commits a unified generation through the data root's `state.json` manifest, pointing to immutable settings, wall, and Recycle Bin DTOs. `Objects` stores managed entries, `Media` stores single images, backgrounds, and native icon series, and `Sources` stores managed albums. `Snapshots`, `Staging`, and `Recovery` hold snapshots and transaction recovery material. `Cache` is rebuildable, and `logs` holds local logs.

Saving and recovery verify actual byte digests and keep generations consistent. Every actual IO operation checks real ancestors and reparse boundaries. New versions automatically upgrade recognized older formats. Unknown higher formats block opening and preserve the data for investigation. Recovery material, entry rereading, and committed media are required for correct recovery. Manual backup and restore require exiting the app and handling the complete data root. A share ZIP covers only the exchange content described above.

External shortcuts, programs, images, and documents are read-only inputs. App-owned code currently runs without background version checks, cloud synchronization, or automatic diagnostic uploads. Windows Shell, UNC or mapped drives, sync-file providers, and bundled SDKs may perform their own network or diagnostic operations.

## Validate changes and report problems

Choose validation to match the change. Model, layout, identity, and transaction changes can start with [Core tests](../src/TileWall.Core.Tests/) and existing compatibility samples. Image decoding, windows, real input, Shell and COM integration, installation, and persistence across restarts also need actual Windows operation. Report build, Core, GUI, upgrade, signing, and environment coverage separately.

For tile editing, check saved properties and that cancellation and save failures preserve the previous data. For import changes, check success, destination-group duplicates, source changes, and cancellation. For shutdown changes, check that unfinished work reaches a consistent outcome. Tests should exercise observable contracts while keeping production validation intact. Operate a shared desktop and app instance serially, use disposable samples, and record the environments and paths actually covered.

Use [Discussions](https://github.com/GitRuozhi/TileWall/discussions) for problems, suggestions, and contribution ideas. Include the version or source commit, distribution type, Windows build, display scale, clear steps, expected and actual results, reproducibility, and relevant logs or a small sample. Logs and wall packages can contain paths, URLs, usernames, and launch arguments. Review them and remove sensitive information before sharing.

When submitting a contribution, explain the problem and scope, and include targeted validation and uncovered areas. For interaction, layout, compatibility, and data-format decisions, discussing the approach with the maintainer first can help. Keep patches focused and retain source attribution, licenses, and historical compatibility samples.

## Join the development team

The public repository is open to everyone, and anyone is welcome to submit issues and pull requests. If you would like to get more involved and explore the full product design, development history, lessons learned, and showcase work, you are welcome to join the development team.

Contact me by email, [Discussions](https://github.com/GitRuozhi/TileWall/discussions), the [QQ group](../README.md#chat-and-feedback), or any other way you can reach me, and let me know you would like to join the TileWall development team. I will invite you to become a developer in the private repository. I look forward to welcoming you.

## License and asset attribution

TileWall project code is covered by the root [MIT License](../LICENSE), with `Copyright (c) 2026 Ruozhi`. Retain the copyright and permission notices when redistributing it.

Third-party software and assets retain their own notices and licenses. Review those terms and any brand rights when reusing them. The main references are [THIRD-PARTY-NOTICES.txt](../src/TileWall/THIRD-PARTY-NOTICES.txt) and [Licenses](../src/TileWall/Licenses/). They record the adapted ReStart taskbar implementation and dependencies such as AsmResolver, .NET, Windows App SDK, Windows SDK, and WebView2.

Bundled website-icon sources, pinned revisions, digests, and licenses are recorded per file in [catalog.json](../src/TileWall/Assets/FallbackIcons/Websites/catalog.json), [NOTICE.txt](../src/TileWall/Assets/FallbackIcons/Websites/NOTICE.txt), and the `SOURCE-LICENSE` and `SOURCE-NOTICE` files in the same directory. First-party system and generic fallback-image sources are recorded in [System/assets.json](../src/TileWall/Assets/FallbackIcons/System/assets.json) and [Generic/assets.json](../src/TileWall/Assets/FallbackIcons/Generic/assets.json). Check and preserve the relevant attribution when reusing code or replacing assets.
