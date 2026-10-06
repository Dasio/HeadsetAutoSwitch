using System;
using System.Threading;
using System.Windows.Forms;

namespace HeadsetAutoSwitch;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, @"Local\HeadsetAutoSwitch", out var firstInstance);
        if (!firstInstance)
        {
            return;
        }

        // Without these an unexpected exception would end the tray app silently.
        Application.ThreadException += (_, e) => Log.Write("unexpected error: " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("unexpected error, exiting: " + e.ExceptionObject);

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayApp());
    }
}
