# 单窗覆盖式设置抽屉

2026-10-26：用户录屏确认上一版独立窗口在收回内容时残留完整的系统轮廓。经讨论，停止维护双窗口裁切方案，改为单窗覆盖式抽屉。

## 设计与原生实现

- 主客户区始终为 820×416 DIP。264 DIP 菜单覆盖主工作区左侧，不挤压或移动仪表，不延长标题栏。主窗 Mica、VU 外观、菜单内容和音频服务保持不变。
- 菜单保留 #161617 实色表面。右缘采用 1 DIP、约 7% 白色的淡亮边，区分相近的深灰表面；没有整圈发光描边或大圆角浮卡。
- 使用 Microsoft.UI.Composition.DropShadow：黑色、BlurRadius=16 DIP、Opacity=0.10、Offset=(3,0,0)。按用户截图反馈，浓度由 42% 大幅降至 10%，同时缩小扩散与偏移，避免右侧黑带。阴影在菜单后方，随整块菜单一起移动；父级窗口内裁切使主要可见投影集中在右侧。
- 按用户反馈移除背景变暗遮罩及其淡入淡出。背景主界面保持原亮度；保留完全透明的外部点击区域，用于点击菜单外收回，采集、分析和仪表绘制继续运行。
- 动效回到旧版 WinUI SplitView 的手感：展开 200 ms、收回 100 ms，两方向均使用 cubic-bezier(0,0.35,0.15,1)。参数直接核对当前依赖 Microsoft.WindowsAppSDK.WinUI 2.3.9 的 generic.xaml：SplitViewPaneAnimationOpenDuration、SplitViewPaneAnimationCloseDuration 及 PaneTransform.TranslateX 的 KeySpline。没有弹簧、超调、内容缩放、文字快照或逐项错峰。
- 位移仍用 WinUI 原生 Vector3KeyFrameAnimation 执行，保留当前 DropShadow 和淡亮边。不再存在遮罩的 ScalarKeyFrameAnimation，没有逐帧 UI 定时器、CompositionTarget.Rendering 窗口操作、SetWindowRgn 或第二个 HWND。
- CompositionScopedBatch 完成通知负责收尾；反向操作通过 this.StartingValue 延续当前呈现位置，世代号屏蔽旧回调，防止重新打开后被旧关闭通知隐藏。
- 系统关闭动画时直接到终态，运行中切换也会结束动画。Tab 在抽屉内循环；Esc、关闭按钮和透明外部点击区域均可收回，结束后归还菜单按钮焦点。
- 所有菜单控件始终在同一 XamlRoot，原生下拉框、文件选择和 ContentDialog 不再涉及跨窗口归属。

当前位移曲线和时长沿用本机旧版菜单实际使用的 SplitView 模板参数；投影和淡亮边仍为本产品设计。实现使用原生 Composition 接口，不重新启用会伸缩主窗的 SplitView 布局。

## 设计依据

- [微软 Motion in Windows](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/motion)：方向一致、反馈直接、避免多余运动，结合本产品频繁开关菜单的场景恢复 SplitView 的直接响应和快速收回。
- [微软 Shadows in Windows apps](https://learn.microsoft.com/en-us/windows/apps/develop/ui/shadows)：阴影表达有意义的层级；只给临时覆盖的设置表面增加投影。
- [WinUI XAML 与 Visual Layer 互操作](https://learn.microsoft.com/en-us/windows/apps/develop/composition/using-the-visual-layer-with-xaml)：原生 DropShadow 与合成动画配合既有 XAML 表面，保留真实文字和交互控件。
- Taste Skill 的应用：保留既有深色仪表性格；通过表面、投影和响应建立层次，避免装饰性弹跳与冗余分段动画。


## “文件”选项卡未绘制的修正

用户录屏及补充明确：初次打开抽屉后“文件”文字缺失，鼠标移上去无效，打开菜单内某个下拉框后才恢复；不能描述为悬停恢复。现象提示绘制失效或内部项目呈现问题，未完成原生调试前不将具体 WinUI 内部原因当成定论。

上一版只把文字放到固定 TextBlock 层，保留 SelectorBar 负责输入，用户手机录像（VID_20261026_220953.mp4）进一步确认 15 秒后文字可见、连续触控板点击却不能选中文件页。不能把文字出现等同于完整交互已经修好。

当前两组选项卡统一改为固定 Grid 中直接实例化的原生 RadioButton。每个按钮自身包含文字、完整透明背景命中区域、焦点和原生选择事件，不再保留 SelectorBar、ItemsRepeater 或独立的可见标签叠层。输入／文件各 84×28 DIP，中间 8 DIP；主界面三项各 76×28 DIP，间距 2 DIP。通过独立 GroupName 保证组间互不影响；保留滑动选中底面与文字颜色过渡。

每组只将当前选项加入 Tab 顺序，左右／上下方向键循环切换，Home／End 到首尾，Enter／原生空格键激活；辅助技术获得按钮名称和原生单选状态。控件模板背景透明但覆盖整个按钮矩形，文字不截获输入。展开完成后再设置菜单焦点。

启动日志增加 Segment bounds、Segment press、Segment click 和 Segment selected，仅记录布局、输入类型与选项，不录制声音；用于区分布局／命中、原生点击及切换回调。没有实窗事件证据前，不将此前 SelectorBar 的具体内部失效机制当成已证实的根因。

原生单选分组依据：[微软 RadioButton 指引](https://learn.microsoft.com/en-us/windows/apps/design/controls/radio-button)。这里直接分组固定控件，避免再次引入内部项目呈现器。

复测重点：重新启动程序后，第一次打开菜单，不先打开任何下拉框，直接用触控板轻触或物理按下选择“文件”，应立即切页；点击文字中心和同一按钮内的左右空白也应生效。反复关闭重开、选中文件页再重开，以及主界面三项的鼠标、触控板、方向键和空格键均应正常。

## 核对范围与实窗复测

本轮仅源码核对、必要编译、发布和 Git 差异检查，没有启动程序、使用 Computer Use 或占用麦克风。不以编译成功代替视觉验收。

请用户复测：

1. 反复开关及动画中反向点击：主窗、标题栏和仪表布局保持不变，抽屉和阴影一起收回，无完整外框残留。
2. 滑入和收回是否恢复旧版菜单的手感，右侧阴影是否足够区分表面；确认主界面不再变暗。
3. 点击菜单外透明区域或 Esc 收回，Tab/Shift+Tab 在菜单内导航；核对输入／文件、下拉框、路径编辑和校准对话框。
4. 最小化／恢复、跨屏缩放及关闭系统动画时核对终态。监看或录制时开关菜单不应改变音频会话。

旧独立窗口方案保留在 Git 历史 a0fbd48，当前不再保留其运行路径。
