using System;
using System.Linq;

namespace CraftStats;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = @"Local\CraftStats.SingleInstance";
    private const string MinimizedArgument = "--minimized";
    private System.Threading.Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            System.Windows.MessageBox.Show(args.Exception.Message, "CraftStats 运行错误", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
                System.IO.File.AppendAllText(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CraftStats", "crash.log"), exception + Environment.NewLine);
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            args.SetObserved();
        };

        if (!PrepareSingleInstance())
        {
            Shutdown();
            return;
        }

        // the Run registry entry appends --minimized when "开机自启时最小化到托盘" is enabled
        var startMinimized = e.Args.Any(arg => arg.Equals(MinimizedArgument, StringComparison.OrdinalIgnoreCase));
        var window = new MainWindow(startMinimized);
        MainWindow = window;
        if (!startMinimized)
            window.Show();
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        if (MainWindow is MainWindow window)
            window.StopBackgroundService();

        if (_ownsSingleInstanceMutex)
        {
            _singleInstanceMutex?.ReleaseMutex();
            _ownsSingleInstanceMutex = false;
        }

        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private bool PrepareSingleInstance()
    {
        _singleInstanceMutex = new System.Threading.Mutex(true, SingleInstanceMutexName, out _ownsSingleInstanceMutex);
        var otherInstances = FindOtherCraftStatsProcesses().ToList();

        if (_ownsSingleInstanceMutex && otherInstances.Count == 0)
            return true;

        var choice = System.Windows.MessageBox.Show(
            "后台已有 CraftStats 进程正在运行。\n\n选择“是”：关闭已有进程，并重新开启当前进程。\n选择“否”：退出当前进程。",
            "CraftStats 已在运行",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);

        if (choice != System.Windows.MessageBoxResult.Yes)
            return false;

        foreach (var process in otherInstances)
            CloseExistingProcess(process);

        if (_ownsSingleInstanceMutex)
            return true;

        try
        {
            _ownsSingleInstanceMutex = _singleInstanceMutex.WaitOne(TimeSpan.FromSeconds(5));
        }
        catch (System.Threading.AbandonedMutexException)
        {
            _ownsSingleInstanceMutex = true;
        }

        if (_ownsSingleInstanceMutex)
            return true;

        System.Windows.MessageBox.Show("无法关闭已有 CraftStats 进程，当前进程将退出。", "CraftStats", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        return false;
    }

    private static IEnumerable<System.Diagnostics.Process> FindOtherCraftStatsProcesses()
    {
        var current = System.Diagnostics.Process.GetCurrentProcess();
        return System.Diagnostics.Process.GetProcessesByName(current.ProcessName)
            .Where(process => process.Id != current.Id);
    }

    private static void CloseExistingProcess(System.Diagnostics.Process process)
    {
        using (process)
        {
            try
            {
                if (process.CloseMainWindow() && process.WaitForExit(3000))
                    return;

                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
            catch
            {
            }
        }
    }
}
