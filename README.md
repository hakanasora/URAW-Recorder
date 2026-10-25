# Portable Recorder

面向多种输入设备的便携录音工具，C# + WinUI 3 + Win2D 原生界面。第一阶段真实音频服务已接入；不再用模拟数据填补输入。当前仍需完成下述真机协同验证。

## 启动与文件

双击 `Start.cmd`，或运行 `artifacts/native-preview/PortableRecorder.Desktop.exe`。保留整个自包含发布目录，不能只拷贝 EXE。

启动即对当前默认输入设备进行**共享 RAW 监看**，录制中固定该设备；待机时监听热插拔并自动选中新接入的输入，当前设备拔除时先结束录音再恢复监看。输入页可选择其他真实设备、刷新设备和选择正式录制的“独占 / 不独占”（首次默认独占，此后记忆用户上次选择，独立于设备）；待机、停止后总是共享监看。RAW 明确不支持时才询问是否允许当前设备在本次运行内使用普通共享监看；这项许可不允许普通非 RAW 正式录制。

录制输出 **32 位浮点 WAV**，采样率和全部声道沿用本次实际输入，超过 4 GiB 自动转 RF64。MP3、FLAC 明确禁用。空路径使用用户“音乐”目录下的 `Portable Recorder`；文件名含日期时间，使用 CreateNew，绝不覆盖。暂停继续监看而不向文件写入样本；继续写同一文件。暂停时可点“丢弃”垃圾桶：先关闭本次文件再移除，只影响本次录音。停止会排空队列并完成文件头，随后释放独占、恢复共享。关闭窗口也先收尾；失败不会报告保存成功。

软件增益同时作用于分析和文件，浮点文件保留超过 ±1 的值，未加限制器。原始输入已经削波无法通过浮点保存修复。校准仅保留直通接口及文件管理入口，标为“预留 / 未应用”，不输出未经校准的 SPL。

## 测量与界面

- 电平为增益后 300 ms 滚动 RMS；PEAK 为逐样本历史峰值，重置在音频块边界执行。静音显示 `−∞`，无有效输入显示 `—`。正常 PEAK 白色粗体、过载红色；没有单独过载灯。
- 原有琥珀 VU 采用平均整流及二阶阻尼模型；1 kHz、峰值 −18 dBFS 正弦稳态为 0 VU，同一信号 RMS 约 −21.01 dBFS。VU 不由 PEAK 驱动。未宣称经过标准认证。
- 频谱使用 NAudio Hann / FFT、真实 `k×Fs/N` 频点、单边幅度补偿、多声道频点功率平均；0 dB/oct 倾斜。快速上升、平滑回落，功率域时间常数 20 / 250 ms。对数频率与明确标示的非均匀 dBFS 纵轴仅改变绘制坐标。没有 Nyquist 以上的伪造数据。
- 音高为 Pitchy 4.1.0 的 MIT McLeod 方法 C# 适配，A4=440 Hz；低清晰度、静音 / 噪声不显示音名，轨迹断开。只用于单音；保留音分尺、四秒轨迹和越界平移。
- 单声道双 VU 镜像、文件仍为单声道；立体声 / 多声道双麦克风图标，提示真实声道。多声道 VU 前两路，RMS 与频谱按实际声道能量平均，PEAK 取全部声道的最大绝对值，文件全部保留。
- 保持固定 820×416 DIP 主界面、264 DIP 左侧设置、现有 Mica 覆盖和 VU 外观。时钟由已保存音频帧数计算，显示百分秒，暂停不增长。绘制跟随 XAML 合成周期，上限 120 Hz（减少动态效果时 60 Hz），实际受显示器和合成器限制；测量频率不随绘制翻倍。指针和频谱仅在视觉层插值，音高视窗按显示时间平滑移动。最小化停止绘制，采集和写盘继续。
- 增益上下拖动，Shift 精调，双击 / Home / 0 归零，方向键 / Shift+方向键调整，Enter / F2 输入，Esc 撤销。`Ctrl+Alt+T` 保留字体试用；模拟场景 F12 已移除。

详细定义、信号链、RAW 边界及验证记录见 [真实音频服务](docs/audio-service.md)。材质见 [材质说明](docs/subtle-materials.md)。第三方来源和许可见 [第三方说明](third_party/README.md)，发布目录也带 `licenses/`。

## 构建与校验

固定 .NET SDK 10.0.402、Windows App SDK 2.5.1、Win2D 1.4.0、NAudio.Core / Wasapi 3.3.0，Windows 11 x64。

```powershell
./tools/build.ps1
./tools/check-audio.ps1
```

本机 SDK：`F:/Codex/.tools/portable-recorder/dotnet/dotnet.exe`。发布到 `artifacts/native-preview`。`./tools/build.ps1 -Launch` 会打开窗体并启动真实共享监看，只有用户准备好时使用。`PortableRecorder.Desktop.exe --no-capture` 仅供无麦克风占用的界面诊断，不生成假信号。

离线检查仅使用合成样本和注入的采集 / 写盘适配，不操作桌面，不使用 Computer Use。数值校验与编译成功不代替实际设备、独占驱动兼容及视觉验收。用户已提供第一轮录音及两段录像，真实录音文件和视频已做后台分析；本轮改动后的热插拔、丢弃、文字渐隐和实际刷新率仍待实窗复测。见 [第一轮反馈与修复](docs/round2-feedback.md)。

## 早期 UI 开发记录（历史）

以下记录来自接入真实服务之前，模拟数据等描述仅指当时版本。

### 2026-10-24：修复发布版黑屏闪退

此前确认能打开的是调试版；发布目录遗漏了应用 `.pri` 资源索引，导致交付 EXE 建出黑窗口后在原生 XAML 层退出。现在显式开启 `EnableMsixTooling` 所提供的资源构建 / 发布任务，同时仍使用 `WindowsPackageType=None`，不生成 MSIX 安装包。编译后的 XAML 随 PRI 发布，不要求有独立的 `App.xbf` 文件。

已直接启动 `artifacts/native-preview/PortableRecorder.Desktop.exe`，确认首轮 Loaded、监看绘制和计时器启动完成；用户已看到修复后的实际发布界面。构建脚本会检查 EXE 与应用 PRI 是否存在，避免只凭编译成功就交付。启动器不变，没有增加自动测试。

启动诊断保存在 `%LOCALAPPDATA%/PortableRecorder/Logs/startup-进程号.log`，记录启动阶段、异常及录制时的时钟布局中心；日志写入失败不掩盖原始错误。

已评审的研究规格和静态参考图保存在 [docs/design](docs/design/README.md)。这些文档是此前设计研究的快照，其中的旧仓库路径和阶段状态属于历史背景。当前可运行内容以 `src/PortableRecorder.Desktop` 为准；旧 HTML 仅保留于 `reference/html-prototype` 作为 VU 绘制参考，不参与应用运行。
