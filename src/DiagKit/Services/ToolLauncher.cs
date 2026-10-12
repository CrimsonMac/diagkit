using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DiagKit.Models;

namespace DiagKit.Services;

public sealed class LaunchOutcome
{
    public bool Started { get; init; }
    public string? Error { get; init; }

    /// <summary>Completes with a message if the tool exits with an error within a few seconds, otherwise with null.</summary>
    public Task<string?> EarlyFailure { get; init; } = Task.FromResult<string?>(null);

    /// <summary>Completes when the tool exits (after registry cleanup).</summary>
    public Task Completion { get; init; } = Task.CompletedTask;
}

public static class ToolLauncher
{
    private static readonly TimeSpan EarlyWindow = TimeSpan.FromSeconds(4);

    public static LaunchOutcome Start(ToolDefinition tool, string exePath, string? extraArguments = null)
    {
        RegistryTraceGuard guard = RegistryTraceGuard.Capture(tool.RegistryTraces, tool.RegistryParents);
        string args = string.Join(" ", new[] { tool.Arguments, extraArguments }).Trim();
        string workDir = Path.GetDirectoryName(exePath) ?? AppPaths.BaseDir;

        Process? process;
        try
        {
            process = StartProcess(exePath, args, workDir);
        }
        catch (Win32Exception ex)
        {
            return new LaunchOutcome { Started = false, Error = Describe(tool.Name, ex) };
        }
        catch (Exception ex)
        {
            return new LaunchOutcome { Started = false, Error = $"{tool.Name} could not start: {ex.Message}" };
        }

        if (process == null)
        {
            // ShellExecute handed off to an existing instance (single-instance tools do this).
            return new LaunchOutcome { Started = true };
        }

        Process proc = process;
        var exited = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnExit()
        {
            int? code = null;
            try
            {
                code = proc.ExitCode;
            }
            catch
            {
                // Exit code unavailable (process handle already gone).
            }

            if (guard.HasWork)
            {
                // Give the tool a moment to finish flushing its settings before deleting them.
                Thread.Sleep(300);
                guard.Cleanup();
            }

            exited.TrySetResult(code);
        }

        proc.EnableRaisingEvents = true;
        proc.Exited += (_, _) => OnExit();
        if (proc.HasExited)
        {
            OnExit();
        }

        Task completion = exited.Task.ContinueWith(_ => proc.Dispose(), TaskScheduler.Default);
        Task<string?> early = WatchEarlyExitAsync(tool.Name, exited.Task);

        return new LaunchOutcome { Started = true, EarlyFailure = early, Completion = completion };
    }

    private static Process? StartProcess(string exePath, string args, string workDir)
    {
        var psi = new ProcessStartInfo(exePath, args)
        {
            UseShellExecute = false,
            WorkingDirectory = workDir,
        };

        try
        {
            // DiagKit is elevated, so CreateProcess hands the child the same admin token.
            return Process.Start(psi);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 740)
        {
            // ERROR_ELEVATION_REQUIRED: only happens when DiagKit itself is not elevated (debug runs).
            var shell = new ProcessStartInfo(exePath, args)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = workDir,
            };
            return Process.Start(shell);
        }
    }

    private static async Task<string?> WatchEarlyExitAsync(string name, Task<int?> exited)
    {
        Task first = await Task.WhenAny(exited, Task.Delay(EarlyWindow)).ConfigureAwait(false);
        if (first != exited)
        {
            return null;
        }

        int? code = await exited.ConfigureAwait(false);
        if (code is null or 0)
        {
            return null;
        }

        return $"{name} closed immediately (exit code 0x{code.Value:X}). The usual cause is a blocked kernel driver: " +
               "Memory Integrity, the vulnerable driver blocklist or antivirus. Check Windows Security > Protection history.";
    }

    private static string Describe(string name, Win32Exception ex) => ex.NativeErrorCode switch
    {
        2 or 3 => $"{name} was not found. It may have been moved or quarantined. Rescan the tools folder.",
        5 => $"Windows denied access to {name}. Antivirus or folder permissions are blocking it.",
        193 => $"{name} is not a valid 64-bit Windows program. The download may be corrupt or the wrong architecture.",
        225 or 226 => $"Windows Security blocked {name} as potentially unwanted. Restore it from Protection history or add an exclusion for the kit folder.",
        1223 => $"Launching {name} was cancelled at the administrator prompt.",
        1260 => $"{name} is blocked by a software restriction policy (AppLocker or SRP) on this PC.",
        4551 => $"Smart App Control or an application control policy blocked {name}.",
        _ => $"{name} could not start: {ex.Message} (error {ex.NativeErrorCode})",
    };
}
