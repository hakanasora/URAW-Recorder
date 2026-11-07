using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace PortableRecorder.Desktop;

internal sealed record NoticeData(string Text, string Details, string? Folder = null);

internal sealed class ServiceNotice : Grid
{
    private readonly FadingText _label = new(11, Design.Muted);
    private readonly Button _button;
    private readonly NativeFade _fade;
    private readonly Func<bool> _animationsEnabled;
    private readonly Func<NoticeData, Task> _activate;
    private NoticeData _requested = new("", "");
    private NoticeData _rendered = new("", "");
    private bool _transitioning;

    public ServiceNotice(Func<bool> animationsEnabled, Func<NoticeData, Task> activate)
    {
        _animationsEnabled = animationsEnabled;
        _activate = activate;
        _fade = new NativeFade(this, animationsEnabled);
        _button = new Button { IsTabStop = false, Content = _label, Padding = new Thickness(6, 2, 6, 2), Background = Design.Brush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        _button.Click += async (_, _) => { var notice = _rendered; if (notice.Text.Length != 0) await _activate(notice); };
        Opacity = 0;
        IsHitTestVisible = false;
        Children.Add(_button);
    }

    public void SetNotice(string text, string details, string? folder = null)
    {
        var next = new NoticeData(text, details, folder);
        if (_requested == next) return;
        _requested = next;
        if (!_animationsEnabled()) { ApplyMotionPreference(); return; }
        if (_transitioning) return;
        if (_rendered.Text.Length == 0) ShowRequested();
        else { _transitioning = true; _fade.To(0, 120, ShowRequested); }
    }

    public void ApplyMotionPreference()
    {
        if (_animationsEnabled()) return;
        _fade.Stop(); _transitioning = false; SetRenderedNotice();
        Opacity = _rendered.Text.Length == 0 ? 0 : 1;
    }

    private void ShowRequested()
    {
        _transitioning = false; SetRenderedNotice();
        if (_rendered.Text.Length != 0) _fade.In();
    }

    private void SetRenderedNotice()
    {
        _rendered = _requested;
        _label.Text = _rendered.Text;
        IsHitTestVisible = _rendered.Text.Length != 0;
        _button.IsTabStop = _rendered.Text.Length != 0;
        AutomationProperties.SetName(_button, _rendered.Text + (_rendered.Folder == null ? "，查看详情" : "，打开保存文件夹"));
    }

    public void ReleaseResources() { _fade.Stop(); _label.ReleaseResources(); }
}
