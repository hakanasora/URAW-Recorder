# Listening mode: native capture evidence and working design

Date: 2026-10-30. Status: native capture service and WinUI frontend implemented. Existing recorder remains intact. No virtual devices, audio rerouting, or driver installation.

## Product boundary

Add a recording/listening selector without moving existing meters. Listening only measures; no writer, file preferences, pause, discard, or recording clock. The sidebar hides File. A neutral #363639 button with #F5F5F7 text/icons starts/stops listening. PC icon + 本机回环 for system capture; real app icon/name for application capture. Name and gain remain stationary. Independent analysis gain defaults to 0 dB and retains the existing -60..+60 range. Never inherit microphone calibration. Existing microphone recorder and exclusive mode remain available.

## Measured evidence

Default render endpoint: SMSL USB DAC, 48 kHz float32 stereo. Test generator: 1 kHz sine, peak .001 (-60 dBFS), expected RMS -63.0103 dBFS. NAudio 3.3.0 native loopback APIs reused. No microphone capture.

| Stage | Endpoint loopback RMS | Process-loopback RMS |
| --- | --- | --- |
| Original Equalizer APO configuration | -70.20887 | -63.01030 |
| Temporary Preamp: -12 dB | -75.01030 | -63.01030 |
| Same temporary APO, Windows master scalar .5 | -85.51890 | -63.01030 |

The same results were obtained for inclusion of the test process and exclusion of the probe process with a separate sibling test player. Exclusion of the probe also excludes its descendants, so the player must not be its child. An initial probe accidentally excluded its child player and received silence; it is not evidence of API failure. Serialization of silence initially failed; the successful repeated probe handles nonfinite statistics explicitly.

Evidence: artifacts/listening-probe/results.json (single process) and global-results.json (exclude own process tree). In this tested chain, process capture precedes Equalizer APO and is independent of master volume; endpoint loopback includes these tested effects. This is not proof for every Windows enhancement, spatial audio, driver, offload path, or hardware effect. No claim of bit-perfect capture: the process API can mix/convert to the requested PCM format.

Original config bytes were backed up under artifacts/listening-probe/config-original.bin and restored in finally; final SHA-256 matches. Original volume scalar 1 and mute setting were restored. Test players stopped. Do not replace the user's EQ configuration as a product feature.

## Native scopes and truthful UI

System process mix (exclude this program tree) can cover processes across endpoints. Endpoint loopback covers one selected render endpoint. They are not identical scopes. Do not offer them as interchangeable pre/post views of a guaranteed identical mix when apps route to different endpoints.

Application mode captures a PID and its child tree; no endpoint chooser applies. Exiting/restarting an app must not silently bind a reused PID or a same-name instance. Do not promise browser-tab capture.

Only label effects that were verified. Spatial sound and the Windows Audio enhancements master switch have not been independently toggled/tested. Do not ship an unconditional all-enhancements-before/after selector on the strength of the EQ probe alone.

The current device's Equalizer APO is a Windows enhancement: the measured bypass is evidence for capture before that enhancement, not merely before the volume slider. No additional master-switch test is required to accept this measured configuration. The UI offers `增强前` (process mix across endpoints) and `增强后 · 输出回环` (selected endpoint), with their different scopes explained next to the controls. The tooltip keeps the unverified spatial/other-driver boundary explicit.

## Required implementation behavior

Reuse PCM decoding, numerical analyzer, and render interpolation. Listening has a separate source/status lifecycle and never invokes the recorder writer or microphone RAW/exclusive/calibration setup. Absence of render packets is not a three-second microphone failure. Distinguish waiting, valid silence, stale data, ended process, and invalid endpoint; do not freeze old live measurements indefinitely or fabricate captured samples. Reset data and history on source changes. Keep the existing RMS/VU/FFT channel definitions and show channel scope in tooltips.

## Sources

- https://learn.microsoft.com/en-us/windows/win32/coreaudio/loopback-recording
- https://raw.githubusercontent.com/microsoft/Windows-classic-samples/main/Samples/ApplicationLoopback/README.md
- https://learn.microsoft.com/en-us/windows/win32/api/audioclient/ne-audioclient-audclnt_streamoptions
- https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/audio-processing-object-architecture

Independent EGM reviewers inspected feasibility, measurement, and UX. All agreed that pre/post volume is not a substitute for pre/post effects; silence semantics and recording-state isolation need explicit implementation.

## Implementation and checks

- `ListeningService` owns native capture and analysis only. It never creates a recording writer or calibration stage. Sources use NAudio's existing loopback builders, with a bounded pooled packet queue and serialized start/stop commands.
- The microphone monitor is released on entering listening. Recording/paused sessions prevent a mode change. Returning to recording resumes shared microphone monitoring and restores its own gain. Listening gain starts at zero and is retained independently within the current run.
- A 500 ms packet-age threshold hides stale measurements and presents `等待音频播放`; it does not diagnose a disconnected microphone or synthesize audio. A returning stream resets analysis continuity. Process creation time guards PID reuse, and exit faults instead of selecting another instance.
- Main header dropdown, dedicated listening drawer, PC/app source identity and neutral start/stop action preserve the existing layout. File tab, calibration, pause/discard and clock are absent in listening. Source name and gain remain stationary. Same-name app instances show PID in the dropdown; executable thumbnails supply the bottom-bar icon with a Fluent fallback.
- Code review corrected early-native-fault cleanup, post-overflow continuity, peak-reset races, recorder disposal when listener cleanup fails, and empty app selection presentation.
- Targeted offline checks: **179 assertions** across `listening`, `capture`, `regression-ui`. These are assertion counts, not measured coverage. No full suite was run.
- Native service start/stop passed for all three sources. At -60 dBFS peak / 1 kHz, system-before and application capture measured RMS **-63.01029977 dBFS**, peak **-59.99999959 dBFS**. Endpoint capture also delivered valid data and stopped cleanly, but other endpoint content/effects were present, so its amplitude is not used as an isolated-tone accuracy result. Evidence: `artifacts/listening-service/results.json`.
- Native UI automation checks use UIAutomation APIs, not Computer Use: work-mode round trip, conditional drawer controls, both system/application start-stop buttons, real application icon/name, and fixed window bounds (1252×635 physical pixels at the current DPI). Native ComboBox mode changes are dispatched after its selection transaction, avoiding a reentrant disable/popup failure. An empty executable file description falls back to the process name. Render captures are under `artifacts/listening-ui/`. Application selection currently lists visible application roots; protected and background-only processes may not be listed.
- Current launcher/publish output: `Start.cmd` → `artifacts/current-preview/PortableRecorder.Desktop.exe`. Existing uncommitted live Windows endpoint-volume notifications and underline removal are preserved.
