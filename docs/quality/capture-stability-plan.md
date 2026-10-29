# 频繁采集不连续：诊断与整改执行单（2026-10-29）

范围：standard。涉及 WASAPI 采集、连续性证据、处理生命周期及回归测试。按 ECC orch-fix-defect 先复现、再修复；本文件为获用户确认的 Gate 1 方案。实施及实测限度见 [结果记录](capture-stability-results.md)。

## 已确认的事实

- 与截图时间对应的 startup-6616.log：2026-10-28 22:05:57.768，Senary 内置麦克风共享 RAW、48 kHz、float32、双声道，出现原生 flags=0x1；随后 ProcessingPipeline 主动抛 CaptureGapException 并结束录音，再恢复共享监看。未发现该事件对应设备移除证据。
- 同次运行启动后一秒内有多次 0x1，后续监看也有；不是仅在录制按钮上发生。
- CaptureContinuity 将任何非首包的原生不连续标志或非零位置差归为同一个布尔结果；现有测试明确要求即使 Delta=0 也保留该标志。正式录制遇到任何该结果均停止，是当前确定的行为，不能把它当成已找到了底层丢包根因。
- 文档声称记录位置差与 QPC，但当前 ProcessingPipeline 实际只写 flags/position/frames/epoch；已有字段未输出。因此不能从旧日志量化损失帧数。
- 采集线程通过 TryWrite 投递，DSP/写盘已分离，没有发现明显的同步 DSP 或磁盘回调阻塞采集。
- MMCSS 服务目前运行，但线程 AvSetMmThreadCharacteristics 注册返回值未检查；这只是明确的防护缺口，不能据此断言用户当时注册失败。

## 按顺序实施

1. 补齐可核对的证据：实际 BufferSize/采样率/默认周期、MMCSS 成功或原生错误、两包帧位置差/QPC、读取及释放耗时、唤醒间隔和队列水位。常态汇总，异常记录，日志在非采集热路径输出，不记录音频内容。确保文档和真实日志一致。
2. 调度及缓冲硬化：检查 MMCSS 返回值，使用 Windows 成熟音频调度方式及明确的回退／错误诊断；核对实际获准缓冲容量，避免把请求的 100 ms 当作设备实际容量。采集前准备可复用资源，缩短持有原生包的时间。保留 RAW、设备格式及原始样本，不通过重采样、降噪或补造样本换稳定性。
3. 以新回归测试复现边界：启动标志、连续位置的标志、可信正向缺口、回跳、时间戳错误、超时、队列积压、录制模式切换首包；验证校准延迟下的暂停／收尾不死锁、不丢已接受尾部、不串会话。先确认旧实现失败，再实施修复。
4. 分级处理：区分原生标志、可信位置缺口、时间戳不确定与设备失效。不得仅凭 Delta=0 就断定无损，也不得直接屏蔽 0x1。先修采集路径，在证据充分时才调整误判规则；真正采样丢失继续明确报告。若想改变“有真实缺口就停录”为“继续录制并标明缺口”，另行明确其文件时间轴与完整性语义，不在本次偷偷改变。
5. 验收与发布：运行受影响后台检查及音频回归；对照旧版本以相同设备、模式和负载做监看／录制稳定性测量，记录缺口次数、缺失帧数、最长采集延迟及缓冲余量。真机测试前在当前对话约定占用麦克风时间，不使用 Computer Use，不自行切换当前设备或关闭用户运行实例。未完成真机验证时不能宣称已根治。
6. 更新 EXE、连续性说明与实际验证结果；审查差异，按 ECC Gate 2 在本地提交前确认。

## 资料

- Microsoft AUDCLNT_BUFFERFLAGS：不连续可来自流状态转换或时序 glitch，不等于物理断连：https://learn.microsoft.com/en-us/windows/win32/api/audioclient/ne-audioclient-_audclnt_bufferflags
- Microsoft GetBuffer 读取与标志语义：https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudiocaptureclient-getbuffer
- Microsoft 原生采集参考：https://github.com/microsoft/Windows-universal-samples/blob/main/Samples/WindowsAudioSession/cpp/WASAPICapture.cpp

计划形成时只完成了日志／源码／成熟实现对照及独立只读审查；后续已实施采集加固、补齐诊断并完成后台短测，间歇故障根因仍未捕获。详见结果记录。
