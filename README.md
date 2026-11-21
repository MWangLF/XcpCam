# XcpCam - FFXIV GPose 可视化运镜编辑插件

<p>
  <a href="https://github.com/MWangLF/XcpCam/releases"><img alt="Release" src="https://img.shields.io/github/v/release/MWangLF/XcpCam?include_prereleases&style=flat-square&label=Release&color=blue"></a>
  <a href="https://github.com/MWangLF/XcpCam/releases"><img alt="Downloads" src="https://img.shields.io/github/downloads/MWangLF/XcpCam/total?style=flat-square&label=Downloads&color=blue"></a>
  <a href="LICENSE"><img alt="License" src="https://img.shields.io/github/license/MWangLF/XcpCam?style=flat-square&label=License&color=blue"></a>
</p>

<p>
  <a href="README.md"><img alt="中文" src="https://img.shields.io/badge/%E4%B8%AD%E6%96%87-blue?style=flat-square"></a>
  <a href="README.en.md"><img alt="English" src="https://img.shields.io/badge/English-lightgrey?style=flat-square"></a>
  <a href="README.ja.md"><img alt="日本語" src="https://img.shields.io/badge/%E6%97%A5%E6%9C%AC%E8%AA%9E-lightgrey?style=flat-square"></a>
  <a href="README.ko.md"><img alt="한국어" src="https://img.shields.io/badge/%ED%95%9C%EA%B5%AD%EC%96%B4-lightgrey?style=flat-square"></a>
</p>

> **当前版本：0.0.6.4（Pre-release）**。插件仍在持续迭代中，欢迎通过 [GitHub Issues](https://github.com/MWangLF/XcpCam/issues) 反馈问题或建议。

**XcpCam** 是一款面向 FFXIV GPose 的 Dalamud 运镜编辑插件。在游戏画面中直接摆放和编辑镜头节点、3D 曲线与骨骼追踪约束，配合按帧计时的多轨时间轴与双层曲线编辑器，既可导出兼容 XAT / Brio 的 `.xcp` 相机文件，也能与 **Brio** 的灵动时间轴（Vivacity）和角色动画实时联动。

## 特色与功能

- **游戏内所见即所得编辑**
  - 在游戏画面中实时显示运镜路径（支持按速度蓝→红着色）、镜头视锥、硬切标记与追踪瞄准线。
  - 直接点选镜头节点、曲线控制点与贝塞尔手柄，变换手柄自动切换作用对象，支持移动 / 旋转 / 缩放、本地坐标、步长吸附以及多选组整体平移与旋转。
  - 预览节点支持右键快捷菜单，进入 GPose 默认自动打开面板，快捷键可在设置页自由绑定。
- **多轨相机时间轴与硬切**
  - 按帧计时的多轨时间轴，支持轨道优先级覆盖、静音 / 独奏、拖动重定时、停留帧、框选多选与多步撤销 / 重做。
  - 同时支持**同轨硬切**与**多轨切换硬切**，实时预览与烘焙到 Brio 均可实现完全瞬切。
- **Blender 风格 3D 曲线与约束**
  - **曲线编辑**：内置贝塞尔曲线、贝塞尔圆环与 NURBS 路径，游戏内直接挤出、细分、切换闭合 / 方向与调节手柄。
  - **跟随路径**：镜头沿 3D 曲线运动，支持在时间轴显示曲线控制点、整条曲线锚定时间轴 / 镜头（保留小数帧）并与速度曲线双向同步。
  - **骨骼追踪**：提供阻尼追踪、锁定追踪、标准追踪三种约束，可绑定任意角色骨骼、调节画面构图偏移与保持取景大小，并支持录制目标骨骼轨迹用于离线导出。
- **双层曲线编辑器（Graph Editor）**
  - **高级曲线**：直接编辑路径进度、位置偏移、未折返旋转（支持多圈旋转）与 FoV 的贝塞尔手柄，精确控制每一段运动。
  - **简单曲线**：保留空间路径，快速调节整段运镜的快慢节奏，支持位置 / 旋转 / FoV 联动或独立调速。
- **深度联动 Brio**
  - 支持绑定 Brio **灵动时间轴（Vivacity）** 或 **角色动画进度条**，自动按秒换算帧率、双向同步总长与播放头。
  - 提供**实时预览镜头**、**联动 XAT 播放**以及**一键烘焙**到 Brio 自由相机轨道（支持撤销）。
- **`.xcp` 导入与可编辑工程**
  - **可编辑工程导出**：导出 `.xcp` 时默认在兼容播放的同时附带完整工程数据，重新导入即可原样恢复多轨时间轴、曲线、锚点与实时骨骼追踪约束。
  - **普通 `.xcp` 智能重建**：导入外部 `.xcp` 时自动生成可编辑的镜头节点与曲线，预览显示误差并可确认疑似硬切，避免逐帧堆砌节点。

## 安装

需要 **Dalamud API 15 / .NET 10**；联动播放、实时预览与烘焙功能需配合 [Brio](https://github.com/Etheirys/Brio) 使用。

### 📦 通过卫月自定义插件库安装（推荐）

1. 在游戏内输入 `/xlsettings` 打开卫月设置，进入 **实验性功能**（Experimental）。
2. 在 **自定义插件库** 中添加以下地址，勾选启用并点击保存：
   ```text
   https://raw.githubusercontent.com/MWangLF/XcpCam/main/repo.json
   ```
3. 打开 `/xlplugins`，搜索并安装 **XcpCam**。
4. 进入 GPose 时会自动打开面板，也可随时输入 `/xcpcam` 手动开关。

### 🛠️ 手动安装（开发插件）

1. 前往 [Releases](https://github.com/MWangLF/XcpCam/releases) 下载最新版本的压缩包并解压。
2. 在 `/xlsettings` → **实验性功能** → `Dev Plugin Locations` 中添加 `XcpCam.dll` 的完整路径。
3. 打开 `/xlplugins` → **开发工具** 启用 XcpCam。

## 反馈与支持

遇到问题或有功能建议，欢迎前往 [Issues](https://github.com/MWangLF/XcpCam/issues) 提交反馈。

## 从源码构建

需要 .NET 10 SDK 与 Dalamud（API 15）：

```bash
set DALAMUD_HOME=%APPDATA%\XIVLauncherCN\addon\Hooks\dev\
dotnet build -c Release
```

## 致谢

- [Brio](https://github.com/Etheirys/Brio)（GPL-3.0）：XAT 相机播放与灵动时间轴
- [XAT](https://github.com/Etheirys/XAT)：`.xcp` 相机文件格式
- [Blender](https://github.com/blender/blender)（GPL-2.0-or-later）：曲线物体、贝塞尔手柄、NURBS、曲线编辑与跟随路径 / 追踪约束设计
- [psenxiv/vista](https://github.com/psenxiv/vista)：弧长表与单调 PCHIP 时间曲线思路
- [ReplayMod / ReplayStudio](https://github.com/ReplayMod/ReplayStudio)：以关键帧时间作为节点的样条运镜思路
- Shoemake (1985/1987)：SQUAD 四元数插值

## 许可证

本项目基于 [GPL-3.0](LICENSE) 许可证开源。
