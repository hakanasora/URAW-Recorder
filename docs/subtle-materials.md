# 灰黑基调的轻量 Mica 材质

日期：2026-10-25。

- 窗口采用系统 MicaBackdrop（Base），不使用 Mica Alt 或 Acrylic。根背景透明，同一原生背景供各区域共享，不新增窗口或模糊循环。
- Display 以 #1B1B1B 为基准，覆盖 alpha 238/255 的中性面层，约 6.7% 底材透出。头部与监看区形成连续表面；顶部 1 DIP 轮廓为白色 alpha 5/255，顶光为白色 alpha 3/255，向下淡至透明。布局与圆角不变。
- 控制区以 #0E0E0E 为基准，覆盖 alpha 242/255 的近黑面层，约 5.1% 底材透出；交界线为白色 alpha 10/255。标题栏沿用显示区面层，标题按钮背景透明。
- 覆盖比例用于抑制壁纸染色，不保证最终像素等于基准 HEX。系统处理激活状态及透明效果策略；不支持 Mica 时使用原有不透明纯色，并省去顶光。
- 设备名通过原生 TextBlock 的透明渐变前景淡出，末尾 24 DIP 按实际文字和视口宽度定位，移除固定底色遮盖。保留录制擦除、完整名称提示及麦克风图标。
- 三种显示共享材质，文字、VU、曲线、布局及模拟交互不变。

必要 Release 编译、发布及 Git 差异检查通过；启动日志记录 Mica 支持状态。材质强度、壁纸切换及非激活状态尚未实窗视觉复核，不运行自动测试或浏览器校验。

实际发布 EXE 已启动，日志记录 Mica=True、Loaded 完成及 820 × 236 DIP 监看首帧，进程正常响应；这不替代材质视觉验收。

依据：[Mica](https://learn.microsoft.com/en-us/windows/apps/design/style/mica)、[系统背景](https://learn.microsoft.com/en-us/windows/apps/develop/ui/system-backdrops)。覆盖比例及微弱顶光是本项目为保留灰黑观感所作的选择。
