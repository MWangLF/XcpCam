# XcpCam

一个 Dalamud 插件：在 GPose 里抓取多个镜头关键帧（例如 Brio 的相机 1、相机 2……），生成平滑运镜，可以导出为 XAT `.xcp` 相机文件，也可以直接绑定 Brio 的灵动时间轴（Vivacity）。插件还能在游戏画面里显示运镜路径和每个镜头的位置。

> 状态：预览版本。离线测试全部通过，游戏内验证仍在进行中。

## 用法

1. 进入 GPose，选中一个角色作为目标（运镜坐标相对于这个角色）。
2. 输入 `/xcpcam` 打开面板。
3. **关键帧**页：在 Brio 中切换或摆好一个镜头，点 **抓取当前镜头**；换下一个镜头后再抓取，重复以上步骤。
4. 选择运镜节奏：
   - **按时间平滑**（默认）：每个镜头在你设定的时间到达；经过镜头时速度连续，不会顿挫。每个镜头还可以单独设置：
     - 经过方式：自动（平滑穿过）、停顿（在这里减速停下）、匀速（到下一个镜头之间保持匀速）
     - 停留：在这个镜头上停留几秒
   - **整体匀速**：忽略各镜头的时间，整条路径以恒定速度运动，可以设置总时长，以及开头缓入、结尾缓出的比例。
5. 输出方式（任选其一）：
   - **导出 .xcp**：在 Brio 相机编辑器中选择 XAT Camera，加载文件后播放。
   - **Brio 时间轴**页，方式 1「联动 XAT」：Brio 时间轴开始播放时，自动从头播放这段运镜。
   - **Brio 时间轴**页，方式 2「烘焙」：把运镜写进 Brio 自由相机的时间轴相机轨道。之后拖动进度条、暂停、循环都能完全同步，也可以撤销。
6. **可视化**页：在游戏画面中显示路径（颜色表示速度：蓝色慢、红色快）和每个镜头的视锥，并提供预览播放头。预览只在画面上绘制，不接管游戏镜头。

## 实现说明

- **抓取**：读取游戏当前正在渲染的相机 `SceneCamera.ViewMatrix`，也就是 Brio 写入的那个矩阵，因此 Brio 的自由相机和普通相机都能抓取。
- **坐标换算**：完全按照 Brio `CutsceneManager.UpdateCamera()` 的公式反推，每次抓取后都会用播放公式重建视图矩阵做自检。
- **路径形状**：向心 Catmull-Rom 曲线穿过所有关键帧，并建立弧长表，可以按"走过的距离"取曲线上的点。
- **节奏**：
  - 按时间平滑：所有关键帧共用一条全局的单调 Hermite 曲线，把时间映射成走过的距离（Fritsch–Butland 切线），所以速度在关键帧处连续，镜头也不会倒退。
  - 整体匀速：中段速度恒定，首尾用 smoothstep 速度曲线缓入缓出，位置、速度、加速度都连续。
- **旋转**：非均匀节点的 SQUAD 四元数样条，角速度在关键帧处连续。FoV 用 Hermite 插值。
- **Brio 联动**：Brio 没有提供时间轴或相机的 IPC，所以通过反射访问 Brio 内部。所有用到的成员名都集中在 `Brio/BrioBridge.cs`，找不到成员时只禁用对应功能，不会崩溃。已在 Brio 0.8.0.12 上核对过。
- **文件格式**：`"XCP1"` + uint32 帧数 + 每帧（pos xyz、rot xyzw、fov），小端序，30fps。Brio 的读取代码会少读最后一帧，所以文件头写入 N+1，并在末尾重复写一次最后一帧。

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

- [Brio](https://github.com/Etheirys/Brio)（GPL-3.0）：XAT 相机播放和灵动时间轴
- [XAT](https://github.com/Etheirys/XAT)：`.xcp` 格式
- [psenxiv/vista](https://github.com/psenxiv/vista)：弧长表和单调 PCHIP 时间曲线的思路
- [ReplayMod / ReplayStudio](https://github.com/ReplayMod/ReplayStudio)：以关键帧时间作节点的样条运镜
- Shoemake (1985/1987)：SQUAD 四元数插值

## 许可证

GPL-3.0
