# 灰黑基调的轻量 Mica 材质

日期：2026-10-25。

## 当前版本：上下异质、底栏透明加强

针对连续两轮效果偏弱的反馈，底栏改用局部 SystemBackdropElement + 自定义 TransportBackdrop，使用 DesktopAcrylicController 的 Thin 模式，实时模糊窗后内容。参数：TintColor #0E0E0E，TintOpacity 0.25，LuminosityOpacity 0.45，额外中性覆盖仅 alpha 24/255。不支持 Acrylic 时保留原纯色；系统默认配置负责透明效果策略、焦点及高对比状态，目标断开时移除并释放控制器。

Display 与标题栏保留 Mica Alt，覆盖 alpha 128/255，底材透出约 49.8%；其布局及顶光不变。底栏的 Acrylic 不是 Mica 透出百分比，最终透明感还取决于原生模糊、亮度及染色参数，不能把 24/255 简化理解成最终 91% 透明。

两区共享原布局但使用不同材质：Display 稳定，底栏更明显地呈现环境色与磨砂透光。设备名字形渐隐与原交互保持。仅必要发布编译、启动及 Git 差异核对；实际透明观感仍待实窗复核。

依据：[系统背景及局部材质区域](https://learn.microsoft.com/en-us/windows/apps/develop/ui/system-backdrops)。

## 上一版：增强 Mica 试版（已被上方方案替代）

用户反馈首版几乎无法察觉。当前改用 MicaBackdrop BaseAlt，允许更明显的壁纸染色；显示区中性覆盖 alpha 160/255（底材透出约 37.3%），控制区覆盖 alpha 144/255（底材透出约 43.5%）。基准色仍是 #1B1B1B / #0E0E0E，实际像素由系统材质与覆盖层共同决定。

显示面板顶光白色 alpha 8/255，向下在 80% 高度处淡至透明；顶部及底栏交界细线白色 alpha 14/255。保持原布局、文字、按钮与绘图；不支持材质时仍使用原不透明纯色。参数集中在 MaterialSurface，便于继续调节强度。

必要发布编译和差异核对通过；启动及材质视觉效果分别核对，不以编译成功代替视觉验收。

## 首版记录（已被上方参数替代）

- 窗口采用系统 MicaBackdrop（Base），不使用 Mica Alt 或 Acrylic。根背景透明，同一原生背景供各区域共享，不新增窗口或模糊循环。
- Display 以 #1B1B1B 为基准，覆盖 alpha 238/255 的中性面层，约 6.7% 底材透出。头部与监看区形成连续表面；顶部 1 DIP 轮廓为白色 alpha 5/255，顶光为白色 alpha 3/255，向下淡至透明。布局与圆角不变。
- 控制区以 #0E0E0E 为基准，覆盖 alpha 242/255 的近黑面层，约 5.1% 底材透出；交界线为白色 alpha 10/255。标题栏沿用显示区面层，标题按钮背景透明。
- 覆盖比例用于抑制壁纸染色，不保证最终像素等于基准 HEX。系统处理激活状态及透明效果策略；不支持 Mica 时使用原有不透明纯色，并省去顶光。
- 设备名通过原生 TextBlock 的透明渐变前景淡出，末尾 24 DIP 按实际文字和视口宽度定位，移除固定底色遮盖。保留录制擦除、完整名称提示及麦克风图标。
- 三种显示共享材质，文字、VU、曲线、布局及模拟交互不变。

必要 Release 编译、发布及 Git 差异检查通过；启动日志记录 Mica 支持状态。材质强度、壁纸切换及非激活状态尚未实窗视觉复核，不运行自动测试或浏览器校验。

实际发布 EXE 已启动，日志记录 Mica=True、Loaded 完成及 820 × 236 DIP 监看首帧，进程正常响应；这不替代材质视觉验收。

依据：[Mica](https://learn.microsoft.com/en-us/windows/apps/design/style/mica)、[系统背景](https://learn.microsoft.com/en-us/windows/apps/develop/ui/system-backdrops)。覆盖比例及微弱顶光是本项目为保留灰黑观感所作的选择。
