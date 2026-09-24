using System.Windows.Threading;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace CraftStats;

/// <summary>Owns the tray NotifyIcon and raises show/exit requests on the UI dispatcher.</summary>
public sealed class TrayIconHost : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private WF.NotifyIcon? _icon;

    public event Action? ShowRequested;
    public event Action? ExitRequested;

    public TrayIconHost(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public void SetVisible(bool visible)
    {
        EnsureCreated();
        _icon!.Visible = visible;
    }

    private void EnsureCreated()
    {
        if (_icon is not null) return;

        var menu = new WF.ContextMenuStrip();
        menu.Items.Add("显示主窗口", null, (_, _) => _dispatcher.BeginInvoke(() => ShowRequested?.Invoke()));
        menu.Items.Add("退出", null, (_, _) => _dispatcher.BeginInvoke(() => ExitRequested?.Invoke()));

        _icon = new WF.NotifyIcon
        {
            Text = "CraftStats",
            Icon = ExtractIcon(),
            ContextMenuStrip = menu,
            Visible = false
        };
        _icon.DoubleClick += (_, _) => _dispatcher.BeginInvoke(() => ShowRequested?.Invoke());
    }

    private static SD.Icon ExtractIcon()
    {
        try
        {
            return SD.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? string.Empty) ?? SD.SystemIcons.Application;
        }
        catch
        {
            return SD.SystemIcons.Application;
        }
    }

    public void Dispose()
    {
        if (_icon is null) return;
        _icon.Visible = false;
        _icon.Dispose();
        _icon = null;
    }
}
