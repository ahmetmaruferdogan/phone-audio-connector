using System.Diagnostics;
using Microsoft.Win32;

namespace PhoneAudioConnector;

/// <summary>Device list with checkboxes, a log, and a tray icon. Closing the window hides it to the tray.</summary>
internal sealed class MainForm : Form
{
    private const int MaxLogChars = 200_000;

    // app.ico is embedded in the .exe via <ApplicationIcon>; fall back to the stock icon if it can't be read.
    private static readonly Icon AppIcon =
        (Environment.ProcessPath is { } exe ? Icon.ExtractAssociatedIcon(exe) : null) ?? SystemIcons.Application;

    private readonly CheckBox _autoStart = new()
        { Text = "Start with Windows", AutoSize = true, Margin = new Padding(12, 7, 3, 3) };

    private readonly FlowLayoutPanel _buttons = new() { Dock = DockStyle.Fill, AutoSize = true };
    private readonly Dictionary<LogCat, ToolStripMenuItem> _categoryItems = new();

    private readonly Button _connectButton = new()
        { Text = "Connect now", AutoSize = true, Padding = new Padding(6, 2, 6, 2) };

    private readonly Button _debugButton = new()
        { Text = "Debug ▾", AutoSize = true, Padding = new Padding(6, 2, 6, 2) };

    // Debug menu
    private readonly ContextMenuStrip _debugMenu = new();

    private readonly Label _hint = new()
    {
        Text = "Tick the devices to keep connected. Pair them first in Windows Bluetooth settings.",
        Dock = DockStyle.Fill,
        AutoSize = true,
        Padding = new Padding(0, 4, 0, 4)
    };

    private readonly TableLayoutPanel _layout = new()
    {
        Dock = DockStyle.Fill,
        ColumnCount = 1,
        RowCount = 4,
        Padding = new Padding(8)
    };

    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        CheckBoxes = true,
        FullRowSelect = true,
        HideSelection = false,
        ShowItemToolTips = true,
        OwnerDraw = true, // only the column header is custom-drawn (for dark mode); rows use default drawing
        BorderStyle = BorderStyle.FixedSingle
    };

    private readonly TextBox _log = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Both,
        WordWrap = false,
        BorderStyle = BorderStyle.FixedSingle,
        Font = new Font("Consolas", 9f)
    };

    private readonly ConnectionManager _manager;

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly ToolStripMenuItem _toFileItem = new("Write log to file") { CheckOnClick = true };
    private readonly NotifyIcon _tray = new();
    private readonly ContextMenuStrip _trayMenu = new();
    private readonly ToolStripMenuItem _verboseItem = new("Verbose logging") { CheckOnClick = true };
    private bool _allowShow; // false while started hidden in the tray
    private bool _exiting;
    private bool _keepDebugMenuOpen;
    private bool _loadingItem; // suppresses ItemChecked while adding rows

    private Palette _palette = Theme.Current;

    public MainForm(bool startHidden, bool forceVerbose)
    {
        _allowShow = !startHidden;

        // Logging first, so everything after this is captured.
        Logger.Verbose = forceVerbose || _settings.Verbose;
        Logger.Categories = forceVerbose ? LogCat.All : _settings.LogCategories;
        Logger.ToFile = _settings.LogToFile;

        _manager = new ConnectionManager(_settings.SelectedIds);

        Text = "Phone Audio Connector";
        Icon = AppIcon;
        Size = new Size(640, 500);
        MinimumSize = new Size(460, 340);
        StartPosition = FormStartPosition.CenterScreen;

        BuildLayout();
        BuildTray();
        BuildDebugMenu();
        WireEvents();

        // Create the window handle now so background events can be marshaled
        // to the UI thread even if the window is never shown.
        _ = Handle;

        LogStartupInfo(startHidden, forceVerbose);
        ApplyTheme();
        _manager.Start();
    }

    private static void LogStartupInfo(bool startHidden, bool forceVerbose)
    {
        Logger.Debug(LogCat.App, $"Exe: {Environment.ProcessPath}");
        Logger.Debug(LogCat.App, $"OS: {Environment.OSVersion.VersionString}, .NET {Environment.Version}, " +
                                 $"{(Environment.Is64BitProcess ? "64" : "32")}-bit");
        Logger.Debug(LogCat.App, $"Args: startHidden={startHidden}, forceVerbose={forceVerbose}");
        Logger.Debug(LogCat.App, $"Log categories: {Logger.Categories}, to file: {Logger.ToFile} ({Logger.FilePath})");
    }

    private void BuildLayout()
    {
        _list.Columns.Add("Device", 300);
        _list.Columns.Add("Status", 180);

        _buttons.Controls.Add(_connectButton);
        _buttons.Controls.Add(_debugButton);
        _buttons.Controls.Add(_autoStart);

        _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        _layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _layout.Controls.Add(_hint, 0, 0);
        _layout.Controls.Add(_list, 0, 1);
        _layout.Controls.Add(_log, 0, 2);
        _layout.Controls.Add(_buttons, 0, 3);

        Controls.Add(_layout);

        _autoStart.Checked = AutoStart.IsEnabled();
    }

    private void BuildTray()
    {
        _trayMenu.Items.Add("Open", null, (_, _) => ShowFromTray());
        _trayMenu.Items.Add("Exit", null, (_, _) => ExitApp());

        _tray.Icon = AppIcon;
        _tray.Text = "Phone Audio Connector";
        _tray.ContextMenuStrip = _trayMenu;
        _tray.DoubleClick += (_, _) => ShowFromTray();
        _tray.Visible = true;
    }

    private void BuildDebugMenu()
    {
        _verboseItem.Checked = Logger.Verbose;
        _toFileItem.Checked = Logger.ToFile;

        _debugMenu.Items.Add(_verboseItem);
        _debugMenu.Items.Add(new ToolStripSeparator());
        _debugMenu.Items.Add(new ToolStripMenuItem("Log these areas:") { Enabled = false });

        var labels = new (LogCat Cat, string Text)[]
        {
            (LogCat.Devices, "Device discovery"),
            (LogCat.Connection, "Connections"),
            (LogCat.Retry, "Retry timer"),
            (LogCat.App, "App / settings / startup"),
            (LogCat.Ui, "UI / theme")
        };
        foreach (var (cat, text) in labels)
        {
            var item = new ToolStripMenuItem("    " + text)
                { CheckOnClick = true, Checked = (Logger.Categories & cat) != 0 };
            item.CheckedChanged += (_, _) => OnCategoryToggled(cat, item.Checked);
            _categoryItems[cat] = item;
            _debugMenu.Items.Add(item);
        }

        _debugMenu.Items.Add(new ToolStripSeparator());
        _debugMenu.Items.Add(_toFileItem);
        _debugMenu.Items.Add("Open log folder", null, (_, _) => OpenLogFolder());
        _debugMenu.Items.Add("Copy log", null, (_, _) =>
        {
            if (_log.TextLength > 0) Clipboard.SetText(_log.Text);
            Logger.Debug(LogCat.Ui, "Log copied to clipboard");
        });
        _debugMenu.Items.Add("Clear log", null, (_, _) => _log.Clear());

        _verboseItem.CheckedChanged += (_, _) =>
        {
            Logger.Verbose = _verboseItem.Checked;
            _settings.Verbose = _verboseItem.Checked;
            _settings.Save();
            UpdateCategoryItems();
            Logger.Info($"Verbose logging {(Logger.Verbose ? "on" : "off")}");
        };

        _toFileItem.CheckedChanged += (_, _) =>
        {
            Logger.ToFile = _toFileItem.Checked;
            _settings.LogToFile = _toFileItem.Checked;
            _settings.Save();
            Logger.Info(Logger.ToFile ? $"Writing log to {Logger.FilePath}" : "Stopped writing log to file");
        };

        // Keep the menu open while ticking checkboxes, so several can be changed at once.
        _debugMenu.ItemClicked += (_, e) =>
            _keepDebugMenuOpen = e.ClickedItem is ToolStripMenuItem { CheckOnClick: true };
        _debugMenu.Closing += (_, e) =>
        {
            if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked && _keepDebugMenuOpen)
                e.Cancel = true;
            _keepDebugMenuOpen = false;
        };

        UpdateCategoryItems();
    }

    private void OnCategoryToggled(LogCat cat, bool on)
    {
        Logger.Categories = on ? Logger.Categories | cat : Logger.Categories & ~cat;
        _settings.LogCategories = Logger.Categories;
        _settings.Save();
        Logger.Info($"Log area {cat} {(on ? "on" : "off")}");
    }

    private void UpdateCategoryItems()
    {
        // Areas only matter while verbose is on; gray them out otherwise.
        foreach (var item in _categoryItems.Values)
            item.Enabled = Logger.Verbose;
    }

    private static void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(Logger.Folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Logger.Folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Info($"Could not open log folder: {ex.Message}");
        }
    }

    private void WireEvents()
    {
        _list.ItemChecked += (_, e) =>
        {
            if (_loadingItem) return;
            Logger.Debug(LogCat.Ui, $"Row \"{e.Item.Text}\" {(e.Item.Checked ? "ticked" : "unticked")}");
            _manager.SetSelected(e.Item.Name, e.Item.Checked);
            _settings.SelectedIds = _manager.SelectedIds.ToList();
            _settings.Save();
        };

        // Header is drawn by hand so it follows the theme; rows use default drawing.
        _list.DrawColumnHeader += DrawHeader;
        _list.DrawItem += (_, e) => e.DrawDefault = true;
        _list.DrawSubItem += (_, e) => e.DrawDefault = true;

        // Last column fills the rest with the width, so no unthemed header area is left on the right.
        _list.Resize += (_, _) => FitStatusColumn();
        _list.ColumnWidthChanged += (_, e) =>
        {
            if (e.ColumnIndex == 0) FitStatusColumn();
        };

        _connectButton.Click += (_, _) =>
        {
            // Selected rows if any, otherwise every ticked row.
            var rows = _list.SelectedItems.Count > 0
                ? _list.SelectedItems.Cast<ListViewItem>()
                : _list.CheckedItems.Cast<ListViewItem>();
            var targets = rows.Where(i => i.Checked).ToList();
            Logger.Debug(LogCat.Ui, $"Connect now clicked: {targets.Count} device(s)");
            foreach (var item in targets)
                _manager.ConnectNow(item.Name);
        };

        _debugButton.Click += (_, _) =>
            _debugMenu.Show(_debugButton, new Point(0, _debugButton.Height));

        _autoStart.CheckedChanged += (_, _) =>
        {
            Logger.Debug(LogCat.Ui, $"Start with Windows {(_autoStart.Checked ? "ticked" : "unticked")}");
            try
            {
                AutoStart.Set(_autoStart.Checked);
            }
            catch (Exception ex)
            {
                Logger.Info($"Could not change startup setting: {ex.Message}");
            }
        };

        _manager.DeviceAdded += (id, name) => Ui(() => AddOrUpdateRow(id, name));
        _manager.DeviceRemoved += id => Ui(() =>
        {
            _list.Items.RemoveByKey(id);
            UpdateTrayText();
        });
        _manager.StateChanged += (id, state) => Ui(() =>
        {
            if (_list.Items[id] is { } item)
                SetStatus(item, state);
            UpdateTrayText();
        });

        Logger.Line += OnLogLine;

        // Follow the Windows light/dark setting while running.
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        // Sleep/resume is a common reason links drop; record it.
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    private void OnLogLine(string line)
    {
        Ui(() => AppendLog(line));
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        Logger.Debug(LogCat.App, $"Power mode: {e.Mode}");
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General)
            Ui(ApplyTheme);
    }

    private void ApplyTheme()
    {
        _palette = Theme.Current;
        var p = _palette;
        Logger.Debug(LogCat.Ui, $"Applying {(p.IsDark ? "dark" : "light")} theme");

        BackColor = p.Back;
        ForeColor = p.Text;
        _layout.BackColor = p.Back;
        _buttons.BackColor = p.Back;
        _hint.ForeColor = p.Muted;

        _list.BackColor = p.Surface;
        _list.ForeColor = p.Text;
        _log.BackColor = p.Surface;
        _log.ForeColor = p.Text;

        _autoStart.ForeColor = p.Text;

        StyleButton(_connectButton, p);
        StyleButton(_debugButton, p);
        StyleMenu(_trayMenu, p);
        StyleMenu(_debugMenu, p);

        Theme.ApplyTitleBar(this, p.IsDark);
        Theme.ApplyScrollbars(_list, p.IsDark);
        Theme.ApplyScrollbars(_log, p.IsDark);

        foreach (ListViewItem item in _list.Items)
            SetStatus(item, _manager.GetState(item.Name));

        FitStatusColumn();
        Invalidate(true);
    }

    private static void StyleButton(Button button, Palette p)
    {
        if (p.IsDark)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.UseVisualStyleBackColor = false;
            button.BackColor = p.Surface;
            button.ForeColor = p.Text;
            button.FlatAppearance.BorderColor = p.Border;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(60, 60, 60);
            button.FlatAppearance.MouseDownBackColor = Color.FromArgb(75, 75, 75);
        }
        else
        {
            button.FlatStyle = FlatStyle.Standard;
            button.UseVisualStyleBackColor = true;
            button.ForeColor = SystemColors.ControlText;
        }
    }

    private static void StyleMenu(ContextMenuStrip menu, Palette p)
    {
        if (p.IsDark)
            menu.Renderer = new ToolStripProfessionalRenderer(new Theme.DarkMenuColors());
        else
            menu.RenderMode = ToolStripRenderMode.ManagerRenderMode;

        foreach (ToolStripItem item in menu.Items)
            item.ForeColor = p.Text;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyTitleBar(this, _palette.IsDark);
    }

    private void DrawHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
    {
        var p = _palette;
        using (var back = new SolidBrush(p.Header))
        {
            e.Graphics.FillRectangle(back, e.Bounds);
        }

        using (var line = new Pen(p.Border))
        {
            e.Graphics.DrawLine(line, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            e.Graphics.DrawLine(line, e.Bounds.Right - 1, e.Bounds.Top + 4, e.Bounds.Right - 1, e.Bounds.Bottom - 5);
        }

        var textBounds = Rectangle.Inflate(e.Bounds, -6, 0);
        TextRenderer.DrawText(e.Graphics, e.Header?.Text, _list.Font, textBounds, p.Muted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    private void FitStatusColumn()
    {
        if (_list.Columns.Count < 2) return;
        var width = _list.ClientSize.Width - _list.Columns[0].Width;
        _list.Columns[1].Width = Math.Max(120, width);
    }

    private void AddOrUpdateRow(string id, string name)
    {
        // Tooltip shows the name used in the log, so the user can match log lines to rows.
        var tooltip = $"Shown in log as: {Logger.TagFor(name)}";

        if (_list.Items[id] is { } existing)
        {
            existing.Text = name;
            existing.ToolTipText = tooltip;
            return;
        }

        _loadingItem = true;
        try
        {
            var item = new ListViewItem(name)
            {
                Name = id,
                Checked = _manager.IsSelected(id),
                UseItemStyleForSubItems = false, // lets the status cell have its own color
                ToolTipText = tooltip
            };
            item.SubItems.Add("");
            _list.Items.Add(item);
            SetStatus(item, _manager.GetState(id));
            Logger.Debug(LogCat.Ui, $"Row added: \"{name}\" ticked={item.Checked}");
        }
        finally
        {
            _loadingItem = false;
        }
    }

    private void SetStatus(ListViewItem item, LinkState state)
    {
        var p = _palette;
        var cell = item.SubItems[1];

        (cell.Text, cell.ForeColor) = state switch
        {
            LinkState.Connected => ("Connected", p.Good),
            LinkState.Waiting => ("Waiting for device...", p.Accent),
            LinkState.Error => ("Error (retrying)", p.Bad),
            _ => item.Checked ? ("Waiting for device...", p.Accent) : ("Not selected", p.Muted)
        };

        item.ForeColor = p.Text;
        item.BackColor = p.Surface;
        cell.BackColor = p.Surface;
    }

    private void UpdateTrayText()
    {
        var connected = _list.Items.Cast<ListViewItem>().Count(i => i.SubItems[1].Text == "Connected");
        var text = connected == 0 ? "Phone Audio Connector" : $"Phone Audio: {connected} connected";
        _tray.Text = text.Length > 63 ? text[..63] : text; // NotifyIcon limit
    }

    private void AppendLog(string msg)
    {
        if (_log.TextLength > MaxLogChars)
            _log.Text = _log.Text[^(MaxLogChars / 2)..];
        _log.AppendText(msg + Environment.NewLine);
    }

    /// <summary>Run on the UI thread. Manager events arrive on background threads.</summary>
    private void Ui(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            if (InvokeRequired) BeginInvoke(action);
            else action();
        }
        catch (ObjectDisposedException)
        {
            // window closed while the event was in flight
        }
        catch (InvalidOperationException)
        {
            // handle destroyed during shutdown
        }
    }

    private void ShowFromTray()
    {
        Logger.Debug(LogCat.Ui, "Window opened from tray");
        _allowShow = true;
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void ExitApp()
    {
        Logger.Info("Exiting");
        _exiting = true;
        _manager.Dispose();
        _tray.Visible = false;
        Application.Exit();
    }

    protected override void SetVisibleCore(bool value)
    {
        // Keep the window hidden on a --minimized start until the user opens it from the tray.
        if (!_allowShow)
        {
            value = false;
            if (!IsHandleCreated) CreateHandle();
        }

        base.SetVisibleCore(value);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // The X button hides to the tray; use the tray menu's Exit to quit.
        if (!_exiting && e.CloseReason == CloseReason.UserClosing)
        {
            Logger.Debug(LogCat.Ui, "Window hidden to tray");
            e.Cancel = true;
            Hide();
            return;
        }

        Logger.Debug(LogCat.App, $"Closing: {e.CloseReason}");
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        // Static events; unsubscribe, or they keep this form alive.
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        Logger.Line -= OnLogLine;

        _manager.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        base.OnFormClosed(e);
    }
}