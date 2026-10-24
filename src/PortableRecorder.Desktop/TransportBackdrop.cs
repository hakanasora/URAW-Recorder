using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace PortableRecorder.Desktop;

/// <summary>Dark but visibly translucent desktop acrylic for the transport region only.</summary>
public sealed class TransportBackdrop : SystemBackdrop
{
    private readonly Dictionary<ICompositionSupportsSystemBackdrop,DesktopAcrylicController> _controllers=new();

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget,XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget,xamlRoot);
        if(!DesktopAcrylicController.IsSupported())return;
        var controller=new DesktopAcrylicController
        {
            Kind=DesktopAcrylicKind.Thin,
            TintColor=Design.Transport, TintOpacity=.25f, LuminosityOpacity=.45f,
            FallbackColor=Design.Transport
        };
        controller.SetSystemBackdropConfiguration(GetDefaultSystemBackdropConfiguration(connectedTarget,xamlRoot));
        controller.AddSystemBackdropTarget(connectedTarget);
        _controllers.Add(connectedTarget,controller);
        StartupLog.Write("Transport material: Desktop Acrylic Thin; tint=.25, luminosity=.45");
    }

    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target,XamlRoot xamlRoot)
    {
        base.OnDefaultSystemBackdropConfigurationChanged(target,xamlRoot);
        if(_controllers.TryGetValue(target,out var controller))
            controller.SetSystemBackdropConfiguration(GetDefaultSystemBackdropConfiguration(target,xamlRoot));
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        if(_controllers.Remove(disconnectedTarget,out var controller))
        {
            controller.RemoveSystemBackdropTarget(disconnectedTarget);
            controller.Dispose();
        }
        base.OnTargetDisconnected(disconnectedTarget);
    }
}
