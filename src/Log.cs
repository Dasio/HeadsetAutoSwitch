using System;
using System.IO;

namespace HeadsetAutoSwitch;

/// <summary>Append-only text log next to the settings; users attach it to bug reports.</summary>
internal static class Log
{
    private const long MaxSize = 1024 * 1024;
    private static readonly object Sync = new();

    public static void Write(string message)
    {
        lock (Sync)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                var file = new FileInfo(AppPaths.Log);
                if (file.Exists && file.Length > MaxSize)
                {
                    File.Copy(AppPaths.Log, AppPaths.Log + ".old", overwrite: true);
                    File.Delete(AppPaths.Log);
                }

                File.AppendAllText(AppPaths.Log, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logging must never take the app down.
            }
        }
    }
}
