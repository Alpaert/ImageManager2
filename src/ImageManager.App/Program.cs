using Avalonia;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace ImageManager.App;

sealed class Program
{
    private const string SingleInstanceMutexName = @"Local\ImageManager2.SingleInstance";

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr window, string text, string caption, uint type);

    [STAThread]
    public static void Main(string[] args)
    {
        using var singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            SingleInstanceMutexName,
            out var isFirstInstance);
        if (!isFirstInstance || IsOlderInstanceRunning())
        {
            MessageBox(
                IntPtr.Zero,
                "ImageManager 已在运行。若看不到窗口，请先在任务管理器中结束旧的 ImageManager.App 进程。",
                "ImageManager",
                0x00000030);
            return;
        }

        try
        {
            BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                "ImageManager_crash.log");
            File.WriteAllText(logPath, $"Crash at {DateTime.Now}:\n{ex}");
            Console.Error.WriteLine(ex.ToString());
            throw;
        }
    }

    private static bool IsOlderInstanceRunning()
    {
        using var currentProcess = Process.GetCurrentProcess();
        var currentStartTime = currentProcess.StartTime;
        var processes = Process.GetProcessesByName("ImageManager.App");
        try
        {
            foreach (var process in processes)
            {
                if (process.Id == Environment.ProcessId)
                    continue;
                try
                {
                    if (process.StartTime < currentStartTime)
                        return true;
                }
                catch { }
            }
            return false;
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
