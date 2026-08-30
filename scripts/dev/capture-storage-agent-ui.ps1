<#
.SYNOPSIS
Launches the BackupMesh Storage Agent tray app in --demo mode, robustly forces it into the
foreground even from a non-interactive automation context, and captures each tab as a PNG for review.

Why this exists: PrintWindow captures against this app were flaky when launched from a background
automation process - SetForegroundWindow is silently ignored by Windows' foreground-lock policy unless
the calling thread is attached to the target window's input queue first. This script does that
explicitly (AttachThreadInput) and verifies each capture actually has real content (not a single flat
color) before trusting it, retrying a few times if not.

On top of that, this WPF app's hardware-accelerated (Direct3D) rendering does not composite into a
capturable surface in this automation context at all - PrintWindow reliably gets a blank client area no
matter how long you wait or how many times you force a repaint. Forcing WPF into software rendering via
the per-user HKCU:\Software\Microsoft\Avalon.Graphics\DisableHWAcceleration registry value makes capture
work on the first attempt. Since that value affects every WPF app for this Windows user account, this
script sets it only for the duration of the capture, always restores whatever value (or absence of one)
it found beforehand - even on failure - and always launches a fresh process afterward, since the setting
is only read at process start.

Usage: pwsh -NoProfile -File scripts/dev/capture-storage-agent-ui.ps1 [-OutDir <path>]
#>
[CmdletBinding()]
param(
    [string]$OutDir = "$env:TEMP\backupmesh-ui-capture",
    [string]$ExePath = "$PSScriptRoot\..\..\storage-agent\tests\BackupMesh.Storage.UiTests\bin\Release\net9.0-windows\BackupMesh.Storage.App.exe"
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class CaptureNative {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint flags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")] public static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    public struct RECT { public int Left, Top, Right, Bottom; }
}
"@

function Force-Foreground([IntPtr]$hwnd) {
    $currentThreadId = [CaptureNative]::GetCurrentThreadId()
    $targetProcessId = 0
    $targetThreadId = [CaptureNative]::GetWindowThreadProcessId($hwnd, [ref]$targetProcessId)
    [CaptureNative]::AttachThreadInput($currentThreadId, $targetThreadId, $true) | Out-Null
    try {
        [CaptureNative]::ShowWindow($hwnd, 9) | Out-Null # SW_RESTORE
        [CaptureNative]::BringWindowToTop($hwnd) | Out-Null
        [CaptureNative]::SetForegroundWindow($hwnd) | Out-Null
    }
    finally {
        [CaptureNative]::AttachThreadInput($currentThreadId, $targetThreadId, $false) | Out-Null
    }
}

# Nudges the window's size (1px and back) to force it to genuinely re-layout and repaint, then asks
# explicitly for a synchronous full repaint. A window that was created without ever being on the
# foreground can otherwise sit with an empty/stale backing surface indefinitely.
function Force-Repaint([IntPtr]$hwnd) {
    $rect = New-Object CaptureNative+RECT
    [CaptureNative]::GetWindowRect($hwnd, [ref]$rect) | Out-Null
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    $SWP_NOMOVE = 0x2; $SWP_NOZORDER = 0x4
    [CaptureNative]::SetWindowPos($hwnd, [IntPtr]::Zero, 0, 0, $width - 1, $height, ($SWP_NOMOVE -bor $SWP_NOZORDER)) | Out-Null
    Start-Sleep -Milliseconds 100
    [CaptureNative]::SetWindowPos($hwnd, [IntPtr]::Zero, 0, 0, $width, $height, ($SWP_NOMOVE -bor $SWP_NOZORDER)) | Out-Null
    $RDW_INVALIDATE = 0x1; $RDW_UPDATENOW = 0x100; $RDW_ALLCHILDREN = 0x80
    [CaptureNative]::RedrawWindow($hwnd, [IntPtr]::Zero, [IntPtr]::Zero, ($RDW_INVALIDATE -bor $RDW_UPDATENOW -bor $RDW_ALLCHILDREN)) | Out-Null
}

# A blank/failed PrintWindow capture renders the OS-drawn title bar correctly but the WPF client area as
# a single flat color - so sampling one point against another anywhere in the window (as an earlier,
# buggy version of this check did) false-positives by picking up the title-bar/body boundary instead of
# actual content. Every sample point here must stay inside the client area (well below the title bar,
# well inside the window edges) and get compared only against other client-area samples.
function Test-HasRealContent([System.Drawing.Bitmap]$bmp) {
    $top = 60
    $bottom = $bmp.Height - 30
    if ($bottom -le $top) { return $false }
    $xs = 40, [int]($bmp.Width * 0.25), [int]($bmp.Width * 0.5), [int]($bmp.Width * 0.75), ($bmp.Width - 40)
    $ys = $top, [int](($top + $bottom) / 2), $bottom
    $seen = New-Object 'System.Collections.Generic.HashSet[int]'
    foreach ($y in $ys) {
        foreach ($x in $xs) {
            if ($x -lt 0 -or $x -ge $bmp.Width) { continue }
            $seen.Add($bmp.GetPixel($x, $y).ToArgb()) | Out-Null
        }
    }
    return $seen.Count -gt 1
}

function Capture-WindowWithRetry([IntPtr]$hwnd, [string]$path, [int]$maxAttempts = 5) {
    for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
        Force-Foreground $hwnd
        Force-Repaint $hwnd
        Start-Sleep -Milliseconds (500 + ($attempt * 1000))
        $rect = New-Object CaptureNative+RECT
        [CaptureNative]::GetWindowRect($hwnd, [ref]$rect) | Out-Null
        $width = $rect.Right - $rect.Left
        $height = $rect.Bottom - $rect.Top
        if ($width -le 0 -or $height -le 0) { continue }
        $bmp = New-Object System.Drawing.Bitmap $width, $height
        $graphics = [System.Drawing.Graphics]::FromImage($bmp)
        $hdc = $graphics.GetHdc()
        [CaptureNative]::PrintWindow($hwnd, $hdc, 2) | Out-Null
        $graphics.ReleaseHdc($hdc)
        $graphics.Dispose()
        if (Test-HasRealContent $bmp) {
            $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
            $bmp.Dispose()
            Write-Host "  OK ($attempt attempt(s)): $path"
            return $true
        }
        $bmp.Dispose()
        Write-Host "  attempt $attempt looked blank, retrying..."
    }
    Write-Host "  FAILED after $maxAttempts attempts: $path"
    return $false
}

function Select-TabAndCapture([System.Windows.Automation.AutomationElement]$root, [IntPtr]$hwnd, [string]$automationId, [string]$fileName) {
    $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $automationId)
    $tab = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if ($null -eq $tab) { Write-Host "  tab not found: $automationId"; return }
    $tab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 400
    Capture-WindowWithRetry $hwnd (Join-Path $OutDir $fileName) | Out-Null
}

# --- Software-rendering toggle: WPF only reads this at process start, so it must be set before the app
# launches and restored (to whatever it was before, including "not present at all") once we're done,
# regardless of success or failure below.
$hwAccelKeyPath = 'HKCU:\Software\Microsoft\Avalon.Graphics'
$hwAccelValueName = 'DisableHWAcceleration'
$originalHwAccelValue = (Get-ItemProperty -Path $hwAccelKeyPath -Name $hwAccelValueName -ErrorAction SilentlyContinue).$hwAccelValueName

if (-not (Test-Path $hwAccelKeyPath)) { New-Item -Path $hwAccelKeyPath -Force | Out-Null }
New-ItemProperty -Path $hwAccelKeyPath -Name $hwAccelValueName -PropertyType DWord -Value 1 -Force | Out-Null
Write-Host "Temporarily forced software rendering (was: $(if ($null -eq $originalHwAccelValue) { '<unset>' } else { $originalHwAccelValue }))"

$startedProc = $null
try {
    New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

    # A process already running was necessarily launched before we set the registry value above (WPF
    # only reads it at startup), so it must be restarted for the capture to work.
    Get-Process -Name 'BackupMesh.Storage.App' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500

    if (-not (Test-Path -LiteralPath $ExePath)) { throw "Executable not found: $ExePath" }
    $startedProc = Start-Process -FilePath $ExePath -ArgumentList '--demo' -PassThru
    Start-Sleep -Seconds 2

    for ($i = 0; $i -lt 20 -and -not $startedProc.MainWindowHandle; $i++) { Start-Sleep -Milliseconds 300; $startedProc.Refresh() }
    $hwnd = $startedProc.MainWindowHandle
    if ($hwnd -eq [IntPtr]::Zero) { throw 'Could not obtain a main window handle.' }

    $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)

    Write-Host "Capturing to $OutDir"
    Select-TabAndCapture $root $hwnd 'OverviewTab' 'overview.png'
    Select-TabAndCapture $root $hwnd 'SourcesMappingsTab' 'sources-mappings.png'
    Select-TabAndCapture $root $hwnd 'DevicesTab' 'devices.png'
    Select-TabAndCapture $root $hwnd 'SettingsTab' 'settings.png'
    Write-Host 'Done.'
}
finally {
    if ($null -ne $startedProc) {
        Stop-Process -Id $startedProc.Id -Force -ErrorAction SilentlyContinue
    }

    if ($null -eq $originalHwAccelValue) {
        Remove-ItemProperty -Path $hwAccelKeyPath -Name $hwAccelValueName -ErrorAction SilentlyContinue
    }
    else {
        New-ItemProperty -Path $hwAccelKeyPath -Name $hwAccelValueName -PropertyType DWord -Value $originalHwAccelValue -Force | Out-Null
    }
    Write-Host 'Restored original hardware-acceleration setting.'
}
