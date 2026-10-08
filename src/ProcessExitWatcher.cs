using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;

namespace HeadsetAutoSwitch;

/// <summary>Calls back when any running process with a given name exits (no polling).</summary>
internal sealed class ProcessExitWatcher : IDisposable
{
    private readonly Action exited;
    private readonly Dictionary<int, Process> watched = [];
    private readonly object sync = new();

    /// <param name="exited">Runs on a thread-pool thread.</param>
    public ProcessExitWatcher(Action exited) => this.exited = exited;

    /// <summary>True if a process named <paramref name="name"/> runs; starts watching it if so.</summary>
    public bool IsRunning(string name)
    {
        var running = false;
        foreach (var process in Process.GetProcessesByName(name))
        {
            // A process that just ended can still be listed while other programs hold a handle to it.
            if (HasExited(process))
            {
                process.Dispose();
                continue;
            }

            running = true;
            lock (sync)
            {
                if (watched.ContainsKey(process.Id))
                {
                    process.Dispose();
                    continue;
                }

                try
                {
                    process.EnableRaisingEvents = true;
                    process.Exited += (_, _) => OnExited(process);
                    watched[process.Id] = process;
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                    process.Dispose(); // exited meanwhile, or not ours to watch
                }
            }
        }

        return running;
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return false; // can't tell (access denied): assume it runs
        }
    }

    private void OnExited(Process process)
    {
        lock (sync)
        {
            watched.Remove(process.Id);
        }

        process.Dispose();
        exited();
    }

    public void Dispose()
    {
        lock (sync)
        {
            foreach (var process in watched.Values)
            {
                process.Dispose();
            }

            watched.Clear();
        }
    }
}
