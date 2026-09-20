# TileWall for Win11

> 一面随时呼出的 Win10 风格磁贴墙：以固定网格组织启动入口，磁贴组共享一张画布并同步翻转；独立于系统开始菜单，不接管 Win 键。

**状态：早期开发中。** 本仓库仅公开源码；产品设计与过程文档不在仓库内维护。

## 技术

- 平台：Windows 10 22H2 / Windows 11
- UI：WinUI 3（Windows App SDK 2.5.1，.NET 10）
- 开发期 unpackaged 直跑；正式发行将使用 MSIX 打包
- 核心逻辑（网格、自动腾位、配置存储）与 UI 分离：`TileWall.Core` 为纯 .NET 类库，配 xunit 单元测试

## 仓库结构

```text
TileWall.sln
src/TileWall              # WinUI 3 应用（墙窗口）
src/TileWall.Core         # 纯逻辑核心库（网格引擎、配置存储）
src/TileWall.Core.Tests   # 单元测试（xunit）
```

## 构建与测试

```bash
dotnet build TileWall.sln
dotnet test src/TileWall.Core.Tests
```

## 许可

待定。方向：源码公开、免费使用（含企业内部办公）、限制商业化分发；正式许可文本发布前，默认保留所有权利。
