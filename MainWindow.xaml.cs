using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.VisualBasic;
using WpfApplication = System.Windows.Application;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfMessageBox = System.Windows.MessageBox;
using WpfTabControl = System.Windows.Controls.TabControl;

namespace CraftStats;

public partial class MainWindow : Window
{
    private readonly AppState _state = new();
    private readonly CraftStatsService _service;
    private readonly TrayIconHost _trayIcon;
    private readonly DispatcherTimer _sortRefreshTimer;
    private readonly DispatcherTimer _chartRefreshTimer;
    private readonly bool _launchedMinimized;
    private bool _initialized;
    private bool _serviceReady;
    private bool _isExitRequested;

    public MainWindow(bool launchedMinimized = false)
    {
        _launchedMinimized = launchedMinimized;
        InitializeComponent();
        DataContext = _state;
        RefreshStatsFilter();
        _service = new CraftStatsService(_state);
        _trayIcon = new TrayIconHost(Dispatcher);
        _trayIcon.ShowRequested += ShowFromTray;
        _trayIcon.ExitRequested += ExitFromTray;
        _sortRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _sortRefreshTimer.Tick += (_, _) => StatSortHelper.Refresh(_state.Stats);
        _chartRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _chartRefreshTimer.Tick += (_, _) => { if (ChartTab.IsSelected) RefreshChart(); };
        _chartRefreshTimer.Start();
        StatsSortCombo.SelectedIndex = 0;
        ChartRangeCombo.SelectedIndex = 1;
        StatsListView.Loaded += (_, _) => SmoothScrollHelper.Enable(StatsListView);
        Closing += MainWindow_Closing;
        Closed += (_, _) =>
        {
            _sortRefreshTimer.Stop();
            _chartRefreshTimer.Stop();
            _trayIcon.Dispose();
        };
        // runs whether the window is shown or hidden at boot; hidden launches skip the paint delay
        Dispatcher.InvokeAsync(InitializeCoreAsync, DispatcherPriority.ApplicationIdle);
    }

    private async void InitializeCoreAsync()
    {
        if (_initialized) return;
        _initialized = true;

        if (IsVisible)
        {
            _state.Dashboard.Status = "界面已就绪，正在启动采集...";
            await Task.Delay(800);
        }

        // hidden launches create the tray icon first so the app always stays reachable and exitable,
        // even when service startup fails
        if (_launchedMinimized)
            _trayIcon.SetVisible(true);

        try
        {
            await _service.StartAsync(this);
        }
        catch (Exception ex)
        {
            _state.Dashboard.Status = "启动失败";
            WpfMessageBox.Show($"统计服务启动失败：{ex.Message}", "CraftStats", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _serviceReady = true;
        ApplyInitialState();
        RefreshStatsFilter();
        RefreshChart();

        if (!_launchedMinimized) return;
        if (_state.Settings.StartMinimizedToTray)
        {
            HideToTray();
        }
        else
        {
            _trayIcon.SetVisible(false);
            Show();
        }
    }

    private void ApplyInitialState()
    {
        ApplyThemePreference();
        StartWithWindowsService.Apply(_state.Settings.StartWithWindows, _state.Settings.StartMinimizedToTray);
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!_serviceReady) return; // saving before the snapshot loads would overwrite it with empty counters
        await _service.SaveNowAsync();
        _state.Dashboard.Status = "已保存";
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (!_serviceReady) return;
        var passphrase = Interaction.InputBox("请输入导出密码", "导出加密包", "");
        if (string.IsNullOrWhiteSpace(passphrase))
            return;
        await _service.SaveNowAsync();
        var path = await _state.Store.ExportEncryptedAsync(_state.Snapshot, passphrase);
        WpfMessageBox.Show($"已导出：{path}", "CraftStats", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (!_serviceReady) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入 CraftStats 数据",
            Filter = "CraftStats 加密包 (*.craftstats)|*.craftstats|所有文件 (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true)
            return;

        var passphrase = Interaction.InputBox("请输入导入密码", "导入加密包", "");
        if (string.IsNullOrWhiteSpace(passphrase))
            return;

        try
        {
            var snapshot = await CraftStatsStore.DecryptAsync(dialog.FileName, passphrase);
            await _service.ImportAsync(snapshot);
            ApplyThemePreference();
            RefreshChart();
            WpfMessageBox.Show("导入完成。", "CraftStats", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show($"导入失败：{ex.Message}", "CraftStats", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (!_serviceReady) return;
        var snapshot = await _state.Store.LoadAsync();
        if (snapshot is null) return;
        await _service.ImportAsync(snapshot);
        ApplyThemePreference();
        RefreshChart();
        WpfMessageBox.Show("已重新加载快照。", "CraftStats", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (!_serviceReady) return;
        var result = WpfMessageBox.Show("确定要把所有统计归零吗？这个操作会覆盖当前本地统计文件。", "归零统计", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
            return;

        await _service.ResetStatsAsync();
        RefreshChart();
        WpfMessageBox.Show("统计已归零。", "CraftStats", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void StartupChanged(object sender, RoutedEventArgs e)
    {
        if (!_serviceReady)
            return;

        _service.ApplySettings();
        _ = _service.SaveNowAsync();
    }

    private void PrefsChanged(object sender, RoutedEventArgs e)
    {
        if (sender is WpfComboBox combo && combo.SelectedValue is string saveModeText && Enum.TryParse<SaveMode>(saveModeText, out var mode))
            _state.Settings.SaveMode = mode;
        if (sender is WpfComboBox themeCombo && themeCombo.SelectedValue is string themeText && Enum.TryParse<AppThemePreference>(themeText, out var theme))
            _state.Settings.ThemePreference = theme;
        if (sender is WpfComboBox closeCombo && closeCombo.SelectedValue is string closeText && Enum.TryParse<CloseBehavior>(closeText, out var closeBehavior))
            _state.Settings.CloseBehavior = closeBehavior;
        ApplyThemePreference();
        RefreshStatsFilter();

        if (!_serviceReady)
            return;

        _service.ApplySettings();
        _ = _service.SaveNowAsync();
    }

    private void ApplyThemePreference()
    {
        WpfApplication.Current.ThemeMode = _state.Settings.ThemePreference switch
        {
            AppThemePreference.Light => ThemeMode.Light,
            AppThemePreference.Dark => ThemeMode.Dark,
            _ => ThemeMode.System
        };
        Dispatcher.InvokeAsync(() => UptimeChart?.InvalidateVisual(), DispatcherPriority.Background);
    }

    private void RefreshStatsFilter()
    {
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(_state.Stats);
        view.Filter = item => item is not StatEntry stat || IsStatVisible(stat.Name);
        view.Refresh();
    }

    private bool IsStatVisible(string name)
    {
        if (!_state.Settings.CollectFileStats && (name.StartsWith("文件读取字节", StringComparison.Ordinal) || name.StartsWith("文件写入字节", StringComparison.Ordinal)))
            return false;
        if (!_state.Settings.CollectNetworkStats && (name == "网络接收字节" || name == "网络发送字节"))
            return false;
        return true;
    }

    private void StatSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not WpfComboBox combo || combo.SelectedItem is not ComboBoxItem item || item.Tag is not string tag)
            return;
        if (!Enum.TryParse<StatSortMode>(tag, out var mode))
            return;
        StatSortHelper.Apply(_state.Stats, mode);
        StatSortHelper.Refresh(_state.Stats);
        _sortRefreshTimer.IsEnabled = mode == StatSortMode.ValueDescending;
    }

    private void DashboardTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource is not WpfTabControl) return;
        if (ChartTab.IsSelected)
            RefreshChart();
    }

    private void ChartRangeChanged(object sender, SelectionChangedEventArgs e)
        => RefreshChart();

    private void RefreshChart()
    {
        if (!_serviceReady)
            return;

        var days = ChartRangeCombo.SelectedItem is ComboBoxItem item && item.Tag is string text && int.TryParse(text, out var parsed)
            ? parsed
            : 14;
        var points = _service.GetDailyUptime(days);
        UptimeChart.Points = points;
        var total = TimeSpan.FromTicks(points.Sum(p => p.Duration.Ticks));
        ChartSummary.Text = $"近 {days} 天总开机 {DurationFormat.Format(total)} · 日均 {DurationFormat.Format(TimeSpan.FromTicks(total.Ticks / Math.Max(1, days)))}";
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_isExitRequested)
            return;

        if (_state.Settings.CloseBehavior == CloseBehavior.Ask)
        {
            var result = WpfMessageBox.Show(
                "关闭 CraftStats 时要最小化到托盘吗？\n\n选择“是”：最小化到托盘，并记住此选择。\n选择“否”：直接关闭，并记住此选择。",
                "关闭 CraftStats",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            _state.Settings.CloseBehavior = result == MessageBoxResult.Yes
                ? CloseBehavior.MinimizeToTray
                : CloseBehavior.Exit;
            if (_serviceReady)
                _ = _service.SaveNowAsync();
        }

        if (_state.Settings.CloseBehavior == CloseBehavior.MinimizeToTray)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        // Closing the window only hides the UI. The collector remains alive in the tray
        // so sampling continues while the interface is closed.
        e.Cancel = true;
        HideToTray();
    }

    private void HideToTray()
    {
        ShowInTaskbar = false;
        Hide();
        _trayIcon.SetVisible(true);
    }

    private void ShowFromTray()
    {
        Show();
        ShowInTaskbar = true;
        WindowState = WindowState.Normal;
        Activate();
        _trayIcon.SetVisible(false);
    }

    private void ExitFromTray()
    {
        _isExitRequested = true;
        _trayIcon.Dispose();
        WpfApplication.Current.Shutdown();
    }

    internal void StopBackgroundService() => _service.Dispose();
}
