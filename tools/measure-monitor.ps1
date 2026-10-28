param(
    [switch]$AllowMicrophone,
    [ValidateSet('电平','频谱','音高')][string]$Mode='频谱',
    [ValidateRange(10,120)][int]$Seconds=25,
    [string]$Label='monitor',
    [switch]$Screenshot
)
$ErrorActionPreference='Stop'
if(!$AllowMicrophone){throw 'This live probe opens the selected microphone for shared monitoring. Run with -AllowMicrophone only when appropriate.'}
if($Label -notmatch '^[a-zA-Z0-9_-]+$'){throw 'Label may contain only letters, digits, underscore and hyphen.'}
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$repoRoot=Split-Path -Parent $PSScriptRoot
$output=Join-Path $repoRoot ('artifacts/monitor-performance/'+$Label+'-'+(Get-Date -Format yyyyMMdd-HHmmss))
New-Item -ItemType Directory -Path $output -Force|Out-Null
$run=Start-Process (Join-Path $repoRoot 'artifacts/native-preview/PortableRecorder.Desktop.exe') -PassThru
try {
    $condition=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$run.Id)
    $window=$null
    for($i=0;$i -lt 60 -and !$window;$i++){
        Start-Sleep -Milliseconds 250
        if($run.HasExited){throw 'Application exited before its window became available.'}
        $window=[System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children,$condition)
    }
    if(!$window){throw 'Application window did not become available.'}
    $name=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$Mode)
    $tab=$window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$name)
    if(!$tab){throw 'Monitor tab not found.'}
    $tab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Seconds 6
    if($Screenshot){
        Add-Type -AssemblyName System.Drawing
        $r=$window.Current.BoundingRectangle
        $bitmap=[Drawing.Bitmap]::new([int]$r.Width,[int]$r.Height)
        $graphics=[Drawing.Graphics]::FromImage($bitmap)
        try{$graphics.CopyFromScreen([int]$r.X,[int]$r.Y,0,0,$bitmap.Size);$bitmap.Save((Join-Path $output 'window.png'))}
        finally{$graphics.Dispose();$bitmap.Dispose()}
    }
    $run.Refresh();$cpu=$run.TotalProcessorTime.TotalSeconds
    $timer=[Diagnostics.Stopwatch]::StartNew()
    Start-Sleep -Seconds $Seconds
    if($run.HasExited){throw 'Application exited during the measurement.'}
    $run.Refresh()
    [ordered]@{Mode=$Mode;Pid=$run.Id;Seconds=$timer.Elapsed.TotalSeconds;
        CpuSeconds=$run.TotalProcessorTime.TotalSeconds-$cpu;
        WorkingSetMiB=$run.WorkingSet64/1MB;PrivateMiB=$run.PrivateMemorySize64/1MB
    }|ConvertTo-Json|Tee-Object (Join-Path $output 'process.json')
} finally {
    if(!$run.HasExited){
        $run.CloseMainWindow()|Out-Null
        if(!$run.WaitForExit(10000)){Write-Warning 'The probe app is still closing. It was not force-terminated; close it before publishing.'}
    }
    $log=Join-Path $env:LOCALAPPDATA "PortableRecorder/Logs/startup-$($run.Id).log"
    if(Test-Path -LiteralPath $log){Copy-Item -LiteralPath $log -Destination (Join-Path $output 'startup.log')}
    Write-Host "Evidence: $output"
}
