# 研究证据、实际案例与适配判断

核查日期：2026-10-24（Asia/Shanghai）。本次只研究公开资料、官方图片和本地已有代码／环境，不安装第三方产品、不运行现有原型。

## 1. 证据怎样使用

- **F（事实）**：官方文档明确说明的功能或规范。
- **O（观察）**：实际查看官方截图得到的界面关系，不能当成厂商公开的设计动机。
- **D（决定）**：针对本工具 的推导、取舍和精确规格。颜色、DIP 和毫秒均属于 D。
- 文档中的「成熟案例」不意味着厂家的一切选择都适合本产品，也不意味着已在最新安装版本上实测。

本研究以近似复现为方法，先理解参考的任务和状态机制，再决定可复制部分。没有可靠原文支持时，不把推断写成作者意图；不凭截图估算厂家 HEX／DIP 后声称是官方规范。

## 2. Apple：结构、检查器、动作和文字

### A1. 设计基础：先结构、后呈现

来源：[Design foundations from idea to interface，WWDC25](https://developer.apple.com/videos/play/wwdc2025/359/)。重点章节：1:11 Structure、8:19 Content、13:28 Visual Design。

F：官方课程从结构、导航、内容进入视觉，讨论内容组织和视觉权重如何共同表达应用性格。

D：本程序保持「配置、观察、录制」三种职责；配置可以退让、观察持续、关键动作常驻。只改一堆控件颜色无法解决职责混杂，因此执行方案先冻结区域和状态，再规定组件样式。

### A2. Inspector：配置依附主任务

来源：[Inspectors in SwiftUI: Discover the details，WWDC23](https://developer.apple.com/videos/play/wwdc2023/10161/)；[HIG Layout](https://developer.apple.com/design/human-interface-guidelines/layout)。

F：Inspector 提供补充主内容的详情，可显隐并按空间适配；布局指导强调对齐、分组、层级和自适应。

D：INPUT／FILE 是参数检查器；本方案选择保留左侧位置，并拆成两页避免长滚动；不照搬 Mail/Finder 的内容导航列表。展开时保持主工作区稳定；空间不足用覆盖面板，避免把仪表缩小。主区先读当前值和单位，再看历史 PEAK；当前过载灯靠当前读数，不再混在历史行。

### A3. Logic Pro：独立时间读数与成组操作

来源：[Customize the control bar](https://support.apple.com/en-me/guide/logicpro/lgcp5bdd6d9d/mac)；[官方主窗口说明与截图，10.7 历史版本](https://support.apple.com/guide/logicpro/logic-pro-main-window-lgcp2a07a994/10.7/mac/11.0)。

F：官方说明控制条可以配置常用控制，LCD 位于控制条中部。

O：实际查看上述官方截图，走带按钮集中、LCD 独立、检查器与主内容之间有清楚边界。此图明确是 10.7，不作为现版像素规范。

D：复制的是读数和动作的稳定关系，时间固定主区中线。保留我们的底栏位置；录制／停止固定右侧，取消飞移，只保留固定组内的宽度交换；不加入多轨、BPM、拍号、编辑器和复杂 LCD。

### A4. Final Cut Pro：值比标签突出

来源：[View a clip’s information](https://support.apple.com/en-gb/guide/final-cut-pro/ver64e70f49/mac)；[Inspectors](https://support.apple.com/en-ae/guide/final-cut-pro/ver15f87af2/mac)。

F：Inspector 可显示、隐藏和修改内容相关信息。

O：实际观察官方 Info inspector 图，标签较弱、值更亮、垂直行节奏紧凑；可编辑字段与只读信息有区别。当前手册页面仍沿用较早界面图，不能称为 2026 现版精确外观。

D：检查器标签／字段值／组标题分为三个文字角色；项目框高度统一，字段不各自套卡片。路径使用标准系统选择器，长文字有完整查看路径。

### A5. Buttons、Motion 与 Voice Memos

来源：[HIG Buttons](https://developer.apple.com/design/human-interface-guidelines/buttons)、[HIG Motion](https://developer.apple.com/design/human-interface-guidelines/motion)、[Voice Memos 创建录音](https://support.apple.com/en-ie/guide/voice-memos/vmaa4b813415/4.0/mac/27)。

F：按钮要求动作明确并提供反馈，动效应帮助理解且支持减少动态效果；Voice Memos 的暂停／继续作用于同一次录音。

D：录制、暂停、继续、停止使用真正的状态模型；暂停冻结时钟但继续监看。动效可打断、不阻塞输入，组宽和右边界稳定。停止不标为「删除」语义。

**冲突处理**：HIG 的样式优先建议与我们的按钮宽度交换并非完全一致。本项目保留组内变形，采用固定录制／停止目标以增强可预测性。不声称任何毫秒值、5 DIP 圆角或我们的左右顺序来自 Apple。

### A6. Typography 与新设计系统

来源：[The details of UI typography，WWDC20](https://developer.apple.com/videos/play/wwdc2020/10175/)；[Get to know the new design system，WWDC25](https://developer.apple.com/videos/play/wwdc2025/356/)。

F：字体的光学字号、字距、行距关系影响不同尺寸的可读性；新的系统设计也区分适合高密度检查器的控件尺寸与形状。

D：采用少量文字角色，数字与界面小字分别处理，取消各处随意字距。Windows 使用本地原生字体，不把学习 Apple 等同于依赖 SF 字体或移植 Liquid Glass。保留小圆角和不透明暗色表面。

## 3. 跨产品组件：Adobe、FabFilter、Resolve

### B1. Camera Raw／Photoshop：参数输入方式共享数值

来源：[Camera Raw 工作区介绍](https://helpx.adobe.com/camera-raw/desktop/get-started/overview-and-setup/introduction-camera-raw.html)、[官方工作区图](https://helpx-prod.scene7.com/is/image/HelpxProd/camera_raw-2?%24png%24=&jpegSize=200&wid=1176)、[Photoshop Panels and menus](https://helpx.adobe.com/photoshop/using/panels-menus.html)。

F：面板分组、滑动与数值输入属于不同操作入口。Photoshop 的 scrubby slider 文档记载左右拖动，Shift 加速；不同 Adobe 产品不能概括成一个统一操作标准。

O：实际查看官方工作区图，主预览与参数栏分开，字段成组、标签与值有层级。图上显示 Camera Raw **13.3**，只用作官网沿用的历史结构证据。

D：复制检查器的稳定宽度和字段组织；增益的所有入口共享一份值。不会把我们上下拖动＋Shift 精调错误称为 Adobe 原样复制，也不复制照片缩略条和工具带。

### B2. FabFilter Pro-Q 4：纵向精调与图表优先

来源：[Overview](https://www.fabfilter.com/help/pro-q/using/overview)、[Knobs](https://www.fabfilter.com/help/pro-q/using/knobs)、[Analyzer](https://www.fabfilter.com/help/pro-q/using/analyzer)、[实际观察的官方总览图](https://www.fabfilter.com/help_pages/pro-q/images/overview%402x.jpg)。

F：旋钮支持纵向拖动和 Shift 精调；其双击是精确输入，与我们已确认的双击归零不同。图中的 band 控制与当前选择有关。

O：图表占主体、网格弱于数据曲线、底栏较低调，局部参数在相关位置出现。

D：复制连续探索与精确调整共享一个参数的机制。本工具保留双击归零，并以 F2／Enter 提供精确编辑。图表保留单一琥珀主色，辉光只响应当前数据；不移入 EQ 节点、多色 band 或钢琴键。

### B3. DaVinci Resolve：区域职责稳定

来源：[Edit 官方产品页](https://www.blackmagicdesign.com/products/davinciresolve/edit)、[实际观察的官方 hero 图](https://images.blackmagicdesign.com/images/products/davinciresolve/edit/hero/hero-still.jpg?_v=1592448885)。

F：官方分别介绍 Inspector、预览、mixing／meter 等不同工作区域。

O：深色外壳依细边界分区，数据和被选内容更醒目，各区域有稳定位置。图片没有可靠的版本标签，不能称为当前版本精确规范。

D：借鉴「低干扰外壳＋稳定区域＋数据优先」，收敛成我们自己的三个职责区。避免靠添加工具栏、mixer 和时间线制造专业感。

## 4. 原生字体与 Windows 平台证据

| 官方来源 | 支持的事实 | 本项目决定 |
|---|---|---|
| [Typography in Windows](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/typography) | Segoe UI Variable 的字重与光学字号轴；XAML 字体与非拉丁文字指导 | Segoe UI Variable 默认英数，Microsoft YaHei UI 中文；图表和控件共用文字角色 |
| [Typography.NumeralAlignment](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.documents.typography.numeralalignment?view=windows-app-sdk-1.8) | 等宽数字是 OpenType 特征，受字体支持影响 | 设置 Tabular 后仍需实际测宽，不能仅凭属性宣称稳定 |
| [Win2D Quick start](https://learn.microsoft.com/en-us/windows/apps/develop/win2d/quick-start) | WinUI C# 中可使用 CanvasControl 绘图、文字和效果；架构需明确 | XAML 主交互＋Win2D 仪表，x64，不用 WebView2 |
| [DPI and DIPs](https://learn.microsoft.com/en-us/windows/apps/develop/win2d/dpi-and-dips) | 逻辑尺寸和像素缩放的关系 | 使用 DIP，跨屏重建 DPI 缓存，不重复乘缩放 |
| [Title bar customization](https://learn.microsoft.com/en-us/windows/apps/develop/title-bar?tabs=winui3) | 系统标题栏行为、窗口管理、原生 TitleBar 控件 | 保留 Windows 窗口按钮和 Snap，内容不覆盖系统命中区 |
| [UISettings.AnimationsEnabled](https://learn.microsoft.com/en-us/uwp/api/windows.ui.viewmanagement.uisettings.animationsenabled?view=winrt-26100) | 可读取系统动画偏好 | 关闭过渡，不停止监看数据和时钟 |
| [Windows App SDK pickers](https://learn.microsoft.com/en-us/windows/apps/develop/files/using-file-folder-pickers) | 1.8 起有 Microsoft.Windows.Storage.Pickers，可选文件和文件夹 | 取得真实完整路径；仅记录元数据，不做校准或写盘 |
| [Unpackaged deployment](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/unpackage-winui-app) | 可使用未打包与自包含分发路径 | 首版输出可运行目录，不把打包签名作为 UI 先决条件 |

只读解析本机字体名称表，`C:\Windows\Fonts\SegUIVar.ttf` family/typographic-family 为 `Segoe UI Variable`。本机还存在 Microsoft YaHei UI、Bahnschrift、Consolas。最终字形清晰度、回退表现和 DPI 效果仍需阶段二的原生窗体验证。

## 5. 工具版本与环境核查

| 对象 | 2026-10-24 核查结果 | 一手入口 |
|---|---|---|
| 项目基线 | main=742c85e；仅有 HTML/CSS/JS 原型，工作区干净 | 本地 git 和源码 |
| Windows | Windows 11 x64，10.0.26220 | 本地 Win32_OperatingSystem 只读查询 |
| 编译工具 | dotnet 入口存在但 SDK 列表为空；默认 VS／Windows SDK 路径、winapp／msbuild 未找到 | 本地只读查询，不等于扫描了全盘自定义安装位置 |
| .NET SDK | 官方下载页主 SDK 为 10.0.402；Runtime 10.0.13（10-13） | [.NET 10 下载](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) |
| Windows App SDK | Stable 2.5.1，09-16；更新的 experimental 不选 | [稳定通道](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-channels)、[NuGet 2.5.1](https://www.nuget.org/packages/Microsoft.WindowsAppSDK/2.5.1) |
| Win2D | 稳定包 1.4.0 存在，依赖含 WinUI 下限 | [NuGet 1.4.0](https://www.nuget.org/packages/Microsoft.Graphics.Win2D/1.4.0) |
| 工具准备方式 | 官方支持 CLI 或 Visual Studio；CLI 自身仍为预览 | [WinUI 入门](https://learn.microsoft.com/en-us/windows/apps/get-started/start-here) |

这些是页面存在性与环境事实。没有运行 NuGet restore、原生 build 或字体实测；包组合的可构建性是阶段二第一个核验事项。

## 6. 可读性计算：没有把「灰」当成单一颜色

基准来源：[W3C Contrast Minimum](https://www.w3.org/WAI/WCAG22/Understanding/contrast-minimum.html)。使用线性化 sRGB 相对亮度与 `(L较亮+0.05)/(L较暗+0.05)`，结果列于执行方案。计算不是运行 UI 测试，也不代表整个样机完成了无障碍审计。

关键推论：同一灰在 `#1B1B1B` 上够清楚，在 `#49494D` 上不够；白字在明亮红底上的对比也不同于红字在暗底上。因此我们保留产品的暗色、红色、蓝色与琥珀身份，同时按文字和表面的角色细分 token。

## 7. 当前原型里应被修正的成熟度问题

| 只读发现 | 阶段二处理 |
|---|---|
| CSS 是 Inter／Segoe 多字体回退，画布另写 Segoe UI | 统一文字资源，中文／英数显式分工 |
| 多数轴字与元数据仅 11 px | 统一为 12 DIP 辅助角色，再在真窗体审阅 |
| 大数字 −0.06 em 字距较紧 | 改为 −0.02 em，不压扁数字 |
| 监看模式藏在自动宽度菜单中 | 改为 240 宽三段选择，能力直接可见，选中项稳定 |
| FILE 位于长滚动尾端 | 检查器 INPUT／FILE 分页，默认当前页内容可见 |
| 起录按钮从中线飞移 | 固定右侧槽位，减少寻找关键操作目标的负担 |
| 同行灯表示当前、PEAK 表示历史 | 当前过载靠当前读数，历史行只放峰值与清除 |
| 聊天宿主内展开侧栏可能挤窄主区 | 原生扩窗或 overlay，主区最小 820 |
| Hover 使用全按钮 brightness | 状态色分开，前景与语义颜色不跟着滤镜变化 |
| 多段历史配色说明互相覆盖 | 本研究表作为新样机唯一规格；HTML 历史文档不在本轮重写 |

## 8. 对大胆重构的补充核查

最新授权明确：保留产品性格之外，布局和交互均可重新设计。采用直接可见的三模式、INPUT／FILE 两页、头部配置入口和固定右侧录制是我们的综合推断，并非厂家给出的精确布局。原生控件使用 [Microsoft SelectorBar](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/selector-bar)，官方用途即少量互斥视图；不使用带关闭／拖出行为的文档 TabView。

几何复核统一为 100＋3＋42＝145 DIP，旧草稿的 143 已纠正。所有图、数值文件和正文使用同一 145 组宽。

## 9. 明确不复现的内容

不复现 Apple 的平台材质和系统资产、其他应用的品牌、复杂工具数量或未公开的设计意图。近似复现集中在已观察／查证的组织和行为，以及适合本产品的组件外观。每个引入项必须能够回答：改善当前什么问题、与哪项原点一致、在什么状态下成立。

用户 v1.2 修订优先于此前研究决定：通用产品定位；动态设备名；恢复既有 VU 外观；正常时隐藏当前过载提示；路径框与浏览按钮分别四角圆角。这些来自本轮用户评审，不归因于参考厂商。
