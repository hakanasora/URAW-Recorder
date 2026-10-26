# 单窗覆盖式设置抽屉

2026-10-26：用户录屏确认上一版独立窗口在收回内容时残留完整的系统轮廓。经讨论，停止维护双窗口裁切方案，改为单窗覆盖式抽屉。

## 设计与原生实现

- 主客户区始终为 820×416 DIP。264 DIP 菜单覆盖主工作区左侧，不挤压或移动仪表，不延长标题栏。主窗 Mica、VU 外观、菜单内容和音频服务保持不变。
- 菜单保留 #161617 实色表面。右缘采用 1 DIP、约 7% 白色的淡亮边，区分相近的深灰表面；没有整圈发光描边或大圆角浮卡。
- 使用 Microsoft.UI.Composition.DropShadow：黑色、BlurRadius=24 DIP、Opacity=0.42、Offset=(8,0,0)。阴影在菜单后方，随整块菜单一起移动；父级窗口内裁切使主要可见投影集中在右侧。
- 背景黑色遮罩目标透明度为 18%，压低背景竞争，不把仪表完全遮黑。采集、分析和仪表绘制继续运行。点击遮罩即可收回，菜单自身不受遮罩染暗。
- 展开 300 ms，位移曲线 cubic-bezier(0.2,0,0.2,1)，明确滑行、柔和落位；关闭 220 ms，cubic-bezier(0.4,0,1,1)，利落离开。没有弹簧、超调、内容缩放、文字快照或逐项错峰。
- 遮罩以 220/160 ms 淡入/淡出。位移用 Vector3KeyFrameAnimation，遮罩用 ScalarKeyFrameAnimation，全部由 WinUI 原生 Composition 执行。没有逐帧 UI 定时器、CompositionTarget.Rendering 窗口操作、SetWindowRgn 或第二个 HWND。
- CompositionScopedBatch 完成通知负责收尾；反向操作通过 this.StartingValue 延续当前呈现位置，世代号屏蔽旧回调，防止重新打开后被旧关闭通知隐藏。
- 系统关闭动画时直接到终态，运行中切换也会结束动画。Tab 在抽屉内循环；Esc、关闭按钮和遮罩均可收回，结束后归还菜单按钮焦点。
- 所有菜单控件始终在同一 XamlRoot，原生下拉框、文件选择和 ContentDialog 不再涉及跨窗口归属。

以上参数是本产品针对仪表界面的设计选择，不宣称为 Windows 内置 SplitView 的默认参数。使用原生合成动画接口，不代表直接复用了 SplitView 控件模板。

## 设计依据

- [微软 Motion in Windows](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/motion)：方向一致、反馈直接、避免多余运动，结合本产品频繁开关菜单的场景采用入慢出快。
- [微软 Shadows in Windows apps](https://learn.microsoft.com/en-us/windows/apps/develop/ui/shadows)：阴影表达有意义的层级；只给临时覆盖的设置表面增加投影。
- [WinUI XAML 与 Visual Layer 互操作](https://learn.microsoft.com/en-us/windows/apps/develop/composition/using-the-visual-layer-with-xaml)：原生 DropShadow 与合成动画配合既有 XAML 表面，保留真实文字和交互控件。
- Taste Skill 的应用：保留既有深色仪表性格；通过表面、投影和响应建立层次，避免装饰性弹跳与冗余分段动画。

## 核对范围与实窗复测

本轮仅源码核对、必要编译、发布和 Git 差异检查，没有启动程序、使用 Computer Use 或占用麦克风。不以编译成功代替视觉验收。

请用户复测：

1. 反复开关及动画中反向点击：主窗、标题栏和仪表布局保持不变，抽屉和阴影一起收回，无完整外框残留。
2. 滑入和收回是否流畅、速度是否舒适，右侧阴影是否足够区分表面，背景仪表是否仍清楚可读。
3. 点击遮罩或 Esc 收回，Tab/Shift+Tab 在菜单内导航；核对输入／文件、下拉框、路径编辑和校准对话框。
4. 最小化／恢复、跨屏缩放及关闭系统动画时核对终态。监看或录制时开关菜单不应改变音频会话。

旧独立窗口方案保留在 Git 历史 a0fbd48，当前不再保留其运行路径。
