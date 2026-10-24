# Portable Recorder

一个面向多种输入设备的进阶便携录音工具。当前为 **C# + WinUI 3 + Win2D 原生前端预览**，使用模拟数据，不是 HTML 或 WebView 窗口。Portable Recorder 是工作名称。

## 打开窗体

双击仓库根目录的 `Start.cmd`，或直接运行：

`artifacts/native-preview/PortableRecorder.Desktop.exe`

发布物为 x64 自包含文件夹，需保留整个目录，不能只拷贝 EXE。`artifacts/` 是本地构建产物，不加入 Git。当前机器开发 SDK 在 `F:/Codex/.tools/portable-recorder/dotnet/`。

## 当前交互

- 电平：原有双灯琥珀 VU、粗体读数、短负号、历史 PEAK 和清除。不再显示独立过载灯或文字，过载由历史 PEAK 红字表示。
- 频谱：右侧替换 VU、左侧保留响度读数与 PEAK；对数频率坐标、金色曲线与渐隐填充；辉光随当前 RMS 改变。
- 音高：音名、频率、渐隐音分尺和四秒轨迹；固定 36 半音跨度，越界后平滑平移。
- “输入／文件”两页侧栏。主客户区固定 820 × 416 DIP，左侧菜单额外扩展 264 DIP，使用原生 SplitView 动画。路径框和浏览按钮分别保留完整四个圆角。禁止拖拽缩放和最大化，主界面不滚动。
- 录制、暂停、继续、停止：原位伸缩与底色过渡，粗体百分秒时钟淡入；设备名随增益左移被擦除，停止后展开；继续和停止交换宽度。“录制中／暂停中”淡换，暂停文字以 2 秒周期呼吸，暂停时监看继续。
- 增益：上下拖动，Shift 精调，双击 / Home / 0 归零；方向键每次 1 dB，Shift + 方向键每次 0.1 dB；Enter / F2 直接输入，Esc 撤销拖动。
- 原生文件夹选择和 TXT 多选；校准列表管理；WAV / FLAC / MP3 与条件品质选择。录制 / 暂停时冻结文件和输入配置。
- `Ctrl+Alt+T` 切换数字字体 Segoe UI Variable / Bahnschrift；`F12` 打开模拟场景。

**范围**：输入设备候选和监看数据均为模拟。底栏名称随所选模拟输入更新，尚未枚举真实设备。文件选择只保存本次会话的路径 / 名称，不读取校准内容，不滤波、不采集、不编码、不输出音频文件。录制按钮仅控制本次 UI 会话和时钟；关闭窗口即结束会话。

## 构建

固定 .NET SDK 10.0.402、Windows App SDK 2.5.1、Win2D 1.4.0，目标 Windows 11 x64。通过 NuGet 恢复构建依赖，无需为本仓库安装完整 Visual Studio。

```powershell
./tools/build.ps1
./tools/build.ps1 -Launch
```

脚本优先使用 `PORTABLE_RECORDER_DOTNET` 指定的 dotnet，其次系统 SDK，最后本机独立 SDK。发布至 `artifacts/native-preview`。

## 校验与设计依据

依用户要求，仅完成必要构建、启动确认及 Git 差异检查；不运行自动测试或浏览器校验。Release 自包含发布零错误、零警告，原生窗口已成功启动，用户也已看到实际窗体。桌面查看工具启动失败，因此没有开展自动 UI 遍历或声称全部视觉状态已验收。

当前选择条使用固定文字与单块 Composition 平移底面；主界面已从 SplitView 内容布局中分离，设备名移到底栏并按需渐隐。发布 EXE 加载及首帧成功；本轮桌面查看工具仍无法初始化，反复切换、菜单闪烁与文字视觉对齐尚未实窗验收。跨屏 DPI、全部边界状态及字体光学微调未逐项实测。最新交互与核对范围见 [固定窗体与动效更新](docs/native-interaction-update.md)。

### 2026-10-24：修复发布版黑屏闪退

此前确认能打开的是调试版；发布目录遗漏了应用 `.pri` 资源索引，导致交付 EXE 建出黑窗口后在原生 XAML 层退出。现在显式开启 `EnableMsixTooling` 所提供的资源构建 / 发布任务，同时仍使用 `WindowsPackageType=None`，不生成 MSIX 安装包。编译后的 XAML 随 PRI 发布，不要求有独立的 `App.xbf` 文件。

已直接启动 `artifacts/native-preview/PortableRecorder.Desktop.exe`，确认首轮 Loaded、监看绘制和计时器启动完成；用户已看到修复后的实际发布界面。构建脚本会检查 EXE 与应用 PRI 是否存在，避免只凭编译成功就交付。启动器不变，没有增加自动测试。

启动诊断保存在 `%LOCALAPPDATA%/PortableRecorder/Logs/startup-进程号.log`，记录启动阶段、异常及录制时的时钟布局中心；日志写入失败不掩盖原始错误。

已评审的研究规格和静态参考图保存在 [docs/design](docs/design/README.md)。这些文档是此前设计研究的快照，其中的旧仓库路径和阶段状态属于历史背景。当前可运行内容以 `src/PortableRecorder.Desktop` 为准；旧 HTML 仅保留于 `reference/html-prototype` 作为 VU 绘制参考，不参与应用运行。
