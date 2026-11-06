# XcpCam

一个 Dalamud 插件：在 GPose 里抓取多个镜头关键帧（例如 Brio 的相机 1、相机 2……），为每个关键帧设定时间，生成平滑运镜并导出 XAT `.xcp` 相机文件，再用 Brio 自带的 XAT 播放器播放。

> 状态：早期版本。离线数学测试全部通过，游戏内验证尚在进行中。

## 用法

1. 进入 GPose，选中一个角色作为目标（`.xcp` 中的坐标相对于这个角色）。
2. 输入 `/xcpcam` 打开面板。
3. 在 Brio 中切换或摆好一个镜头，点 **抓取当前镜头**；换下一个镜头后再抓取，重复以上步骤。
4. 在表格中设置每个关键帧的时间（秒），以及到下一个关键帧的缓动方式（Linear / Smooth / In / Out）。
5. 点 **导出 .xcp**。
6. 在 Brio 相机编辑器中选择 XAT Camera，加载文件后播放。播放时请使用同一个目标角色，并开启 FoV。

## 实现说明

- 抓取：读取游戏当前正在渲染的相机 `SceneCamera.ViewMatrix`，也就是 Brio 写入的那个矩阵，因此 Brio 的自由相机和普通相机都适用。
- 坐标换算：完全按照 Brio `CutsceneManager.UpdateCamera()` 的公式反推，`pos = inv(baseRot)·(world − basePos)`，`rot = inv(baseRot)·worldRot`。每次抓取后都会用播放公式重建视图矩阵来自检。
- 插值：30fps。位置使用向心 Catmull-Rom 曲线，只有两个关键帧时退化为直线；旋转使用 Slerp 并做半球对齐；FoV 使用 Catmull-Rom 并限制在关键帧的范围内。
- 文件格式：`"XCP1"` + uint32 帧数 + 每帧（pos xyz、rot xyzw、fov），小端序。Brio 的读取代码会少读最后一帧，所以文件头写入 N+1，并在末尾重复写一次最后一帧。

## 构建

需要 .NET 10 SDK 和 XIVLauncher / Dalamud（API 15）。

```
dotnet build -c Release
```

如果 Dalamud 不在默认位置（例如国服），请先设置 `DALAMUD_HOME`：

```
set DALAMUD_HOME=%APPDATA%\XIVLauncherCN\addon\Hooks\dev\
```

将 `bin/Release/XcpCam.dll` 添加到 Dalamud 设置 → 实验性功能 → Dev Plugin Locations。

离线测试（不需要游戏）：

```
cd Tests
dotnet run -c Release
```

## 致谢

- [Brio](https://github.com/Etheirys/Brio)（GPL-3.0）：XAT 相机的播放实现
- [XAT](https://github.com/Etheirys/XAT)：`.xcp` 格式

## 许可证

GPL-3.0
