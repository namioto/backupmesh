using System.Diagnostics;
using System.Globalization;

namespace BackupMesh.Storage.Service;

// Sample only when an automatic backup is ready. Failed probes allow the backup to proceed.
public sealed class HostLoadProbe
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastSample;
    private bool _lastBusy;

    public async Task<bool> IsBusyAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) return false;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (DateTimeOffset.UtcNow - _lastSample < TimeSpan.FromMinutes(1)) return _lastBusy;
            _lastSample = DateTimeOffset.UtcNow;
            _lastBusy = await SampleAsync(cancellationToken);
            return _lastBusy;
        }
        finally { _gate.Release(); }
    }

    private static async Task<bool> SampleAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var processInfo = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            processInfo.ArgumentList.Add("-NoProfile");
            processInfo.ArgumentList.Add("-NonInteractive");
            processInfo.ArgumentList.Add("-Command");
            processInfo.ArgumentList.Add("$c=(Get-CimInstance Win32_PerfFormattedData_PerfOS_Processor -Filter \"Name='_Total'\").PercentProcessorTime; $d=(Get-CimInstance Win32_PerfFormattedData_PerfDisk_PhysicalDisk -Filter \"Name='_Total'\").PercentDiskTime; Write-Output \"$c,$d\"");
            using var process = Process.Start(processInfo) ?? throw new InvalidOperationException("Load probe could not start.");
            using var kill = timeout.Token.Register(() => { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var values = output.Trim().Split(',');
            return process.ExitCode == 0 && values.Length == 2
                && double.TryParse(values[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var cpu)
                && double.TryParse(values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var disk)
                && (cpu >= 80 || disk >= 80);
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested) { return false; }
    }
}
