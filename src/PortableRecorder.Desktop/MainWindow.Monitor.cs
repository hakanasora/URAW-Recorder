using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PortableRecorder.Audio;

namespace PortableRecorder.Desktop;

public sealed partial class MainWindow
{
    private bool _exclusiveNoticeShown;
    private bool _exclusiveNoticePending;
    private bool _monitorCalibrationNoticeShown;
    private bool _monitorCalibrationNoticePending;

    private async Task SetRecordingModeAsync(int index)
    {
        if (index < 0 || _closing) return;
        var mode = index == 0 ? RecordingMode.Exclusive : RecordingMode.Shared;
        var previous = _state.RecordMode;
        if (mode == previous) return;
        _gainAlignGeneration++;
        await AudioCommand(async () =>
        {
            try { await _state.Audio.SetRecordingModeAsync(mode); }
            finally { if (_state.RecordMode != previous) SaveRecordingMode(); }
        });
        if (_closing) return;
        _recordModeBox.SelectedIndex = _state.RecordMode == RecordingMode.Exclusive ? 0 : 1;
        MaybeEnqueueExclusiveNotice();
    }

    private void MaybeEnqueueExclusiveNotice()
    {
        if (_state.RecordMode == RecordingMode.Exclusive && !_exclusiveNoticeShown)
            EnqueueRecordingNotice(ref _exclusiveNoticePending, ShowExclusiveNotice);
    }

    private void SaveRecordingMode()
    {
        try { RecordingPreferences.Save(_state.RecordMode, exclusiveMonitoringNoticeShown: _exclusiveNoticeShown); _modePreferenceWarning = null; }
        catch (Exception error) { _modePreferenceWarning = "录制模式偏好未保存：" + error.Message; StartupLog.Write(_modePreferenceWarning); }
    }

    private async Task ToggleRecordingMonitorAsync(bool enable)
    {
        if (_closing || _state.IsListening || WorkModeChanging) return;
        try
        {
            await _state.SetRecordingMonitorEnabledAsync(enable);
            if (_closing) return;
            _monitorControl.Refresh();
            if (_state.RecordingMonitorEnabled) MaybeEnqueueMonitorCalibrationNotice();
        }
        catch (Exception error)
        {
            StartupLog.Write("Live monitor toggle failed: " + error);
            _state.Notify();
            _monitorControl.Refresh();
            if (!_closing) await Message(enable ? "无法开启监听" : "关闭监听失败", AudioEngine.Friendly(error));
        }
    }

    private void OnLiveMonitorFaulted(string reason)
    {
        if (_closing) return;
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (_closing) return;
            _state.Notify();
            _monitorControl.Refresh();
            await Message("监听已关闭", reason);
        });
    }

    private void EnqueueRecordingNotice(ref bool pending, Func<Task> show)
    {
        if (_closing || pending || !_coordinator.CanEnqueueRecordingNotice()) return;
        pending = DispatcherQueue.TryEnqueue(async () => await show());
    }

    private async Task ShowRecordingNoticeDialogAsync(string? title, string text, string closeButtonText, Action onOpened)
    {
        var body = Design.Text(text);
        body.TextWrapping = TextWrapping.Wrap;
        var dialog = new ContentDialog
        {
            XamlRoot = _root.XamlRoot,
            Title = title,
            Content = body,
            CloseButtonText = closeButtonText,
            RequestedTheme = ElementTheme.Dark
        };
        dialog.Opened += (_, _) => onOpened();
        await ShowDialog(dialog, ModalKind.RecordingNotice);
    }

    private async Task ShowExclusiveNotice()
    {
        try
        {
            if (_state.RecordMode != RecordingMode.Exclusive || _exclusiveNoticeShown || !_coordinator.CanEnqueueRecordingNotice()) return;
            await ShowRecordingNoticeDialogAsync(
                null,
                """
                独占模式下，即使没在录音，其他应用也无法使用此麦克风。

                Windows 的麦克风软件增益在独占模式下失效。电平偏低时，可在主界面调高增益，留意峰值以免削波。
                """,
                "关闭",
                () =>
                {
                    _exclusiveNoticeShown = true;
                    SaveRecordingMode();
                });
        }
        catch (Exception error) { StartupLog.Write("Exclusive notice dialog failed: " + error); }
        finally { _exclusiveNoticePending = false; }
    }

    private void MaybeEnqueueMonitorCalibrationNotice()
    {
        if (_monitorCalibrationNoticeShown || !_state.RecordingMonitorEnabled) return;
        var status = _state.Audio.Calibration;
        if (!status.Applied || status.LatencyFrames <= 0) return;
        EnqueueRecordingNotice(ref _monitorCalibrationNoticePending, ShowMonitorCalibrationNotice);
    }

    private async Task ShowMonitorCalibrationNotice()
    {
        try
        {
            if (_closing || _monitorCalibrationNoticeShown || !_state.RecordingMonitorEnabled) return;
            var status = _state.Audio.Calibration;
            if (!status.Applied || status.LatencyFrames <= 0 || !_coordinator.CanEnqueueRecordingNotice()) return;
            int latencyMs = (int)Math.Round(status.LatencyFrames * 1000.0 / Math.Max(1, status.SampleRate));
            string profile = string.IsNullOrWhiteSpace(status.Name) ? "当前频响校准" : $"“{status.Name}”";
            await ShowRecordingNoticeDialogAsync(
                "启用校准会引入监听延迟",
                $"当前已启用频响校准（{profile}），监听信号会经过校准滤波器，将引入约 {latencyMs} ms 的固定监听延迟。\n\n这不会影响录音文件与频响校准精度；若希望获得更低的实时监听延迟，可在设置中临时禁用校准。",
                "我知道了",
                () => _monitorCalibrationNoticeShown = true);
        }
        catch (Exception error) { StartupLog.Write("Monitor calibration notice dialog failed: " + error); }
        finally { _monitorCalibrationNoticePending = false; }
    }
}
