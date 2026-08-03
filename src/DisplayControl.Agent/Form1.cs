using System.Runtime.InteropServices;
using DisplayControl.Contracts;
using Microsoft.AspNetCore.SignalR.Client;

namespace DisplayControl.Agent;

public sealed partial class Form1 : Form
{
    private const uint WmSysCommand = 0x0112;
    private const uint ScMonitorPower = 0xF170;
    private const uint SmtoAbortIfHung = 0x0002;
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;
    private const uint EsDisplayRequired = 0x00000002;
    private static readonly nint HwndBroadcast = 0xffff;

    // 与网页控制台、服务器管理器共用的视觉基调：浅灰底、白色面板、蓝色主操作。
    private static readonly Color AppBackground = Color.FromArgb(245, 248, 253);
    private static readonly Color Surface = Color.White;
    private static readonly Color PrimaryText = Color.FromArgb(15, 23, 42);
    private static readonly Color SecondaryText = Color.FromArgb(100, 116, 139);
    private static readonly Color Primary = Color.FromArgb(37, 99, 235);
    private static readonly Color PrimaryHover = Color.FromArgb(29, 78, 216);
    private static readonly Color Accent = Color.FromArgb(3, 105, 161);

    private readonly List<BlackOverlayForm> _overlays = [];
    private readonly System.Windows.Forms.Timer _heartbeatTimer = new();
    private readonly System.Windows.Forms.Timer _reconnectTimer = new();
    private readonly System.Windows.Forms.Timer _localOverrideTimer = new();
    private readonly TextBox _serverUrl = new();
    private readonly TextBox _deviceId = new();
    private readonly TextBox _deviceName = new();
    private readonly NumericUpDown _heartbeatSeconds = new();
    private readonly Button _connect = new();
    private readonly Button _disconnect = new();
    private readonly Button _chooseInstallLocation = new();
    private readonly Button _installStartup = new();
    private readonly Button _removeStartup = new();
    private readonly Button _showOverlay = new();
    private readonly Button _hideOverlay = new();
    private readonly TextBox _log = new();
    private readonly Label _status = new();
    private readonly SemaphoreSlim _commandLock = new(1, 1);

    private HubConnection? _connection;
    private AgentSettings _settings = AgentSettings.Load().Normalize();
    private DisplayState _state = DisplayState.Unknown;
    private bool _stateVerified;
    private string? _stateDetail;
    private bool _manualDisconnect = true;
    private bool _connecting;
    private bool _closingBlackOverlay;
    private DateTimeOffset _lastRemoteCommandAtUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastLocalOverrideAtUtc = DateTimeOffset.MinValue;

    public Form1()
    {
        InitializeComponent();
        BuildUi();
        LoadSettingsIntoForm();
        _heartbeatTimer.Tick += (_, _) => _ = SendHeartbeatAsync();
        _reconnectTimer.Interval = 10_000;
        _reconnectTimer.Tick += (_, _) => _ = ReconnectIfNeededAsync();
        _localOverrideTimer.Interval = 1_000;
        _localOverrideTimer.Tick += (_, _) => _ = CheckLocalOverrideAndReportAsync();
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (Environment.GetCommandLineArgs().Any(arg => string.Equals(arg, "--minimized", StringComparison.OrdinalIgnoreCase)))
        {
            WindowState = FormWindowState.Minimized;
            ShowInTaskbar = false;
            await ConnectAsync(showError: false);
        }
    }

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        await DisconnectAsync(manual: true);
        HideBlackOverlay(updateState: false);
        SetExecutionState(active: false);
        base.OnFormClosing(e);
    }

    private void BuildUi()
    {
        Text = "屏控 Windows Agent";
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        DoubleBuffered = true;
        ClientSize = new Size(1000, 800);
        MinimumSize = new Size(940, 740);
        AutoScroll = true;
        AutoScrollMinSize = new Size(900, 710);
        BackColor = AppBackground;
        Padding = new Padding(20);
        Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Regular, GraphicsUnit.Point);

        var shell = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Surface,
            BorderStyle = BorderStyle.FixedSingle,
            Padding = new Padding(24, 20, 24, 18)
        };
        Controls.Add(shell);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            BackColor = Surface
        };
        // A 方向：关键区域固定高度，日志区域吃掉剩余空间。
        // 这样高 DPI、小窗口或现场投屏时，标题、输入框和按钮不会再被挤压裁切。
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 108));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 202));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 132));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shell.Controls.Add(root);

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            BackColor = Surface
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
        root.Controls.Add(header, 0, 0);

        var titlePanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Surface
        };
        titlePanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        titlePanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        header.Controls.Add(titlePanel, 0, 0);
        titlePanel.Controls.Add(new Label
        {
            Text = "显示屏控制 Agent",
            Dock = DockStyle.Fill,
            Font = new Font("Microsoft YaHei UI", 18.5F, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = PrimaryText,
            TextAlign = ContentAlignment.BottomLeft
        }, 0, 0);
        titlePanel.Controls.Add(new Label
        {
            Text = "Windows 显示端 · Agent 16.6",
            Dock = DockStyle.Fill,
            Font = new Font("Microsoft YaHei UI", 10.5F, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = SecondaryText,
            TextAlign = ContentAlignment.TopLeft
        }, 0, 1);

        _status.Text = "未连接";
        _status.Dock = DockStyle.Top;
        _status.Height = 36;
        _status.Margin = new Padding(0, 12, 0, 0);
        _status.Padding = new Padding(12, 0, 12, 0);
        _status.TextAlign = ContentAlignment.MiddleCenter;
        _status.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold, GraphicsUnit.Point);
        _status.ForeColor = SecondaryText;
        _status.BackColor = Color.FromArgb(241, 245, 249);
        header.Controls.Add(_status, 1, 0);

        var formGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 4,
            Padding = new Padding(0, 8, 0, 8),
            BackColor = Surface
        };
        formGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        formGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < 4; row++)
        {
            formGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        }
        root.Controls.Add(formGrid, 0, 1);

        AddRow(formGrid, 0, "服务器地址", _serverUrl);
        AddRow(formGrid, 1, "设备编号", _deviceId);
        AddRow(formGrid, 2, "设备名称", _deviceName);
        _heartbeatSeconds.Minimum = 5;
        _heartbeatSeconds.Maximum = 300;
        _heartbeatSeconds.Dock = DockStyle.Left;
        _heartbeatSeconds.Width = 150;
        AddRow(formGrid, 3, "心跳秒数", _heartbeatSeconds);

        var actionGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 2,
            Padding = new Padding(0, 6, 0, 8),
            BackColor = Surface
        };
        actionGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
        actionGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
        actionGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
        actionGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
        actionGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        actionGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        root.Controls.Add(actionGrid, 0, 2);

        ConfigureButton(_connect, "保存并连接", ButtonKind.Primary);
        ConfigureButton(_disconnect, "断开连接");
        ConfigureButton(_chooseInstallLocation, "选择安装位置", ButtonKind.Accent);
        ConfigureButton(_installStartup, "设置开机启动", ButtonKind.Accent);
        ConfigureButton(_removeStartup, "取消开机启动");
        ConfigureButton(_showOverlay, "本机黑屏测试", ButtonKind.Accent);
        ConfigureButton(_hideOverlay, "解除黑屏");
        _connect.Click += async (_, _) => await ConnectAsync();
        _disconnect.Click += async (_, _) => await DisconnectAsync(manual: true);
        _chooseInstallLocation.Click += (_, _) => ChooseInstallLocation();
        _installStartup.Click += async (_, _) => await InstallStartupAsync();
        _removeStartup.Click += (_, _) => RemoveStartup();
        _showOverlay.Click += async (_, _) =>
        {
            ShowBlackOverlay();
            var verified = await VerifyBlackOverlayAsync(TimeSpan.FromSeconds(_settings.OverlayOperationTimeoutSeconds));
            SetObservedState(
                DisplayState.BlackOverlay,
                verified,
                verified ? "本机黑幕遮罩已通过像素确认。" : "本机黑幕遮罩已打开，但像素确认超时。");
        };
        _hideOverlay.Click += (_, _) => HideBlackOverlay();
        actionGrid.Controls.Add(_connect, 0, 0);
        actionGrid.Controls.Add(_disconnect, 1, 0);
        actionGrid.Controls.Add(_chooseInstallLocation, 2, 0);
        actionGrid.Controls.Add(_installStartup, 3, 0);
        actionGrid.Controls.Add(_showOverlay, 0, 1);
        actionGrid.Controls.Add(_hideOverlay, 1, 1);
        actionGrid.Controls.Add(_removeStartup, 2, 1);

        root.Controls.Add(new Label
        {
            Text = "运行日志",
            Dock = DockStyle.Fill,
            Padding = new Padding(0, 8, 0, 0),
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = PrimaryText,
            Font = new Font("Microsoft YaHei UI", 10.5F, FontStyle.Bold, GraphicsUnit.Point)
        }, 0, 3);

        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.Dock = DockStyle.Fill;
        _log.BorderStyle = BorderStyle.FixedSingle;
        _log.BackColor = AppBackground;
        _log.ForeColor = Color.FromArgb(51, 65, 85);
        _log.Font = new Font("Consolas", 9.5F, FontStyle.Regular, GraphicsUnit.Point);
        root.Controls.Add(_log, 0, 4);
        AcceptButton = _connect;
        ApplyAccessibilityLabels();
    }

    private static void AddRow(TableLayoutPanel root, int row, string labelText, Control control)
    {
        control.Margin = new Padding(0, 4, 0, 4);
        control.Dock = DockStyle.Fill;
        if (control is TextBox textBox)
        {
            textBox.BorderStyle = BorderStyle.FixedSingle;
            textBox.Font = new Font("Microsoft YaHei UI", 10.5F, FontStyle.Regular, GraphicsUnit.Point);
        }

        root.Controls.Add(new Label
        {
            Text = labelText,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Microsoft YaHei UI", 10.5F, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = PrimaryText
        }, 0, row);
        root.Controls.Add(control, 1, row);
    }

    private enum ButtonKind
    {
        Primary,
        Secondary,
        Accent
    }

    private enum StatusKind
    {
        Neutral,
        Info,
        Success,
        Error
    }

    private static void ConfigureButton(Button button, string text, ButtonKind kind = ButtonKind.Secondary)
    {
        button.Text = text;
        button.Dock = DockStyle.Fill;
        button.Margin = new Padding(4);
        button.MinimumSize = new Size(0, 46);
        button.FlatStyle = FlatStyle.Flat;
        button.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold, GraphicsUnit.Point);
        button.UseVisualStyleBackColor = false;
        button.AccessibleName = text;

        switch (kind)
        {
            case ButtonKind.Primary:
                button.BackColor = Primary;
                button.ForeColor = Color.White;
                button.FlatAppearance.BorderColor = Primary;
                button.MouseEnter += (_, _) => button.BackColor = PrimaryHover;
                button.MouseLeave += (_, _) => button.BackColor = Primary;
                break;
            case ButtonKind.Accent:
                button.BackColor = Surface;
                button.ForeColor = Accent;
                button.FlatAppearance.BorderColor = Color.FromArgb(125, 211, 252);
                button.MouseEnter += (_, _) => button.BackColor = Color.FromArgb(240, 249, 255);
                button.MouseLeave += (_, _) => button.BackColor = Surface;
                break;
            default:
                button.BackColor = Surface;
                button.ForeColor = PrimaryText;
                button.FlatAppearance.BorderColor = Color.FromArgb(203, 213, 225);
                button.MouseEnter += (_, _) => button.BackColor = AppBackground;
                button.MouseLeave += (_, _) => button.BackColor = Surface;
                break;
        }
    }

    private void SetStatus(string text, StatusKind kind)
    {
        _status.Text = text;
        switch (kind)
        {
            case StatusKind.Info:
                _status.ForeColor = Primary;
                _status.BackColor = Color.FromArgb(219, 234, 254);
                break;
            case StatusKind.Success:
                _status.ForeColor = Color.FromArgb(22, 101, 52);
                _status.BackColor = Color.FromArgb(220, 252, 231);
                break;
            case StatusKind.Error:
                _status.ForeColor = Color.FromArgb(185, 28, 28);
                _status.BackColor = Color.FromArgb(254, 226, 226);
                break;
            default:
                _status.ForeColor = SecondaryText;
                _status.BackColor = Color.FromArgb(241, 245, 249);
                break;
        }
    }

    private void SetBusy(bool busy)
    {
        _connect.Enabled = !busy;
        _chooseInstallLocation.Enabled = !busy;
        _installStartup.Enabled = !busy;
        _disconnect.Enabled = !busy;
        _connect.Text = busy ? "正在连接…" : "保存并连接";
        _chooseInstallLocation.Text = busy ? "请稍候…" : "选择安装位置";
        _installStartup.Text = busy ? "请稍候…" : "设置开机启动";
    }

    private void ApplyAccessibilityLabels()
    {
        _serverUrl.AccessibleName = "服务器地址";
        _deviceId.AccessibleName = "设备编号";
        _deviceName.AccessibleName = "设备名称";
        _heartbeatSeconds.AccessibleName = "心跳秒数";
        _chooseInstallLocation.AccessibleName = "选择安装位置";
        _log.AccessibleName = "运行日志";
        _status.AccessibleName = "连接状态";
    }

    private void LoadSettingsIntoForm()
    {
        _serverUrl.Text = _settings.ServerUrl;
        _deviceId.Text = _settings.DeviceId;
        _deviceName.Text = _settings.DeviceName;
        _heartbeatSeconds.Value = _settings.HeartbeatSeconds;
    }

    private AgentSettings ReadSettingsFromForm() =>
        new AgentSettings(
            _serverUrl.Text,
            _deviceId.Text,
            _deviceName.Text,
            (int)_heartbeatSeconds.Value,
            _settings.SimulationMode,
            _settings.UseBlackOverlay,
            _settings.UseMonitorStandby,
            _settings.PreventSystemSleep,
            _settings.OverlayOperationTimeoutSeconds,
            _settings.DisplayConfirmationTimeoutSeconds,
            _settings.BlackPixelThreshold)
        .Normalize();

    private async Task ConnectAsync(bool showError = true)
    {
        if (_connecting)
        {
            return;
        }

        _connecting = true;
        _manualDisconnect = false;
        _reconnectTimer.Stop();
        await DisposeCurrentConnectionAsync();
        _settings = ReadSettingsFromForm();
        _settings.Save();

        var connection = new HubConnectionBuilder()
            .WithUrl($"{_settings.ServerUrl}/hubs/agent")
            .WithAutomaticReconnect(new ForeverRetryPolicy())
            .Build();
        _connection = connection;

        connection.On<DisplayCommand>("ExecuteCommand", command =>
            OnUi(() => _ = ExecuteCommandAsync(command)));
        connection.Reconnecting += error =>
        {
            OnUi(() =>
            {
                SetStatus("正在重连…", StatusKind.Info);
                AppendLog(error is null ? "正在重连服务器。" : $"正在重连服务器：{error.Message}");
            });
            return Task.CompletedTask;
        };
        connection.Reconnected += async _ =>
        {
            await RegisterAsync();
            await SendHeartbeatAsync();
            OnUi(() =>
            {
                SetStatus("已连接", StatusKind.Success);
                AppendLog("已重连服务器。");
            });
        };
        connection.Closed += error =>
        {
            OnUi(() =>
            {
                var message = error is null ? "已断开" : $"连接异常：{error.Message}";
                SetStatus(error is null ? "已断开" : "连接异常", error is null ? StatusKind.Neutral : StatusKind.Error);
                AppendLog(message);
                if (!_manualDisconnect && ReferenceEquals(_connection, connection))
                {
                    _heartbeatTimer.Stop();
                    _localOverrideTimer.Stop();
                    _reconnectTimer.Start();
                }
            });
            return Task.CompletedTask;
        };

        SetBusy(true);
        SetStatus("正在连接…", StatusKind.Info);
        try
        {
            await connection.StartAsync();
            await RegisterAsync();
            _heartbeatTimer.Interval = _settings.HeartbeatSeconds * 1000;
            _heartbeatTimer.Start();
            _localOverrideTimer.Start();
            SetExecutionState(active: true);
            SetStatus("已连接", StatusKind.Success);
            AppendLog($"已连接：{_settings.ServerUrl}");
            await SendHeartbeatAsync();
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_connection, connection))
            {
                await DisposeCurrentConnectionAsync();
            }

            SetStatus("连接失败", StatusKind.Error);
            AppendLog($"连接失败：{ex.Message}。Agent 会继续自动重试。");
            if (!_manualDisconnect)
            {
                _reconnectTimer.Start();
            }

            if (showError)
            {
                MessageBox.Show(
                    $"无法连接到服务器：{ex.Message}\r\n\r\nAgent 会保留配置并继续自动重试。",
                    "屏控 Agent",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }
        finally
        {
            _connecting = false;
            SetBusy(false);
        }
    }

    private async Task ReconnectIfNeededAsync()
    {
        if (_manualDisconnect || _connecting || _connection?.State == HubConnectionState.Connected)
        {
            return;
        }

        await ConnectAsync(showError: false);
    }

    private async Task InstallStartupAsync()
    {
        _settings = ReadSettingsFromForm();
        _settings.Save();
        CreateStartupShortcut();
        AppendLog("已设置开机启动：使用当前 Agent 文件夹，不复制项目文件。");
        await ConnectAsync();
        MessageBox.Show(
            "已设置开机启动。Agent 会从当前文件夹启动，项目文件不会复制到 LocalAppData。",
            "屏控 Agent",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private void ChooseInstallLocation()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "请选择 Windows Agent 安装位置。程序会在所选目录下创建一个 Agent 文件夹。",
            ShowNewFolderButton = true,
            SelectedPath = Directory.Exists(@"A:\") ? @"A:\" : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            var sourceRoot = ResolvePackageRoot();
            var packageName = Path.GetFileName(sourceRoot.TrimEnd('\\'));
            var destinationRoot = Path.GetFullPath(dialog.SelectedPath).TrimEnd('\\');
            var destination = Path.GetFullPath(Path.Combine(destinationRoot, packageName)).TrimEnd('\\');
            if (destination.Equals(sourceRoot, StringComparison.OrdinalIgnoreCase) ||
                destination.StartsWith(sourceRoot.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("安装位置不能选择当前 Agent 文件夹或其子文件夹。");
            }

            if (Directory.Exists(destination) &&
                Directory.EnumerateFileSystemEntries(destination).Any())
            {
                throw new InvalidOperationException($"目标文件夹已存在且不为空：{destination}");
            }

            CopyDirectory(sourceRoot, destination);
            AppendLog($"Agent 已复制到新安装位置：{destination}");
            MessageBox.Show(
                $"Agent 已复制到：\r\n{destination}\r\n\r\n请到新位置启动 Agent；如果需要开机启动，请在新位置点击“设置开机启动”。",
                "屏控 Agent",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            System.Diagnostics.Process.Start("explorer.exe", $"\"{destination}\"");
        }
        catch (Exception ex)
        {
            AppendLog($"选择安装位置失败：{ex.Message}");
            MessageBox.Show(
                $"复制 Agent 失败：{ex.Message}",
                "屏控 Agent",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void RemoveStartup()
    {
        var shortcut = StartupShortcutPath();
        if (File.Exists(shortcut))
        {
            File.Delete(shortcut);
        }

        AppendLog("已取消开机启动。");
        MessageBox.Show(
            "已取消开机启动。当前 Agent 文件夹内的文件会保留。",
            "屏控 Agent",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private static string ResolvePackageRoot()
    {
        var appRoot = AppContext.BaseDirectory.TrimEnd('\\');
        var parent = Directory.GetParent(appRoot)?.FullName;
        if (!string.IsNullOrWhiteSpace(parent) &&
            string.Equals(Path.GetFileName(appRoot), "app", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(Path.Combine(parent, "启动Agent.cmd")))
        {
            return parent;
        }

        return appRoot;
    }

    private static void CopyDirectory(string sourceRoot, string destinationRoot)
    {
        Directory.CreateDirectory(destinationRoot);
        foreach (var directory in Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceRoot, directory);
            Directory.CreateDirectory(Path.Combine(destinationRoot, relative));
        }

        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceRoot, file);
            var destination = Path.Combine(destinationRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    private static void CreateStartupShortcut()
    {
        var root = AppContext.BaseDirectory.TrimEnd('\\');
        var exe = Path.Combine(root, "DisplayControl.Agent.exe");
        var dll = Path.Combine(root, "DisplayControl.Agent.dll");
        var dotnet = Path.Combine(root, "dotnet", "dotnet.exe");
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        dynamic link = shell.CreateShortcut(StartupShortcutPath());
        if (File.Exists(dotnet))
        {
            link.TargetPath = dotnet;
            link.Arguments = $"\"{dll}\" --minimized";
        }
        else
        {
            link.TargetPath = exe;
            link.Arguments = "--minimized";
        }

        link.WorkingDirectory = root;
        link.Description = "Display Control Agent";
        link.Save();
    }

    private static string StartupShortcutPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            "DisplayControlAgentV16.lnk");

    private async Task DisconnectAsync(bool manual)
    {
        if (manual)
        {
            _manualDisconnect = true;
        }

        _heartbeatTimer.Stop();
        _localOverrideTimer.Stop();
        _reconnectTimer.Stop();
        await DisposeCurrentConnectionAsync();
        SetExecutionState(active: false);
        SetStatus("未连接", StatusKind.Neutral);
    }

    private async Task DisposeCurrentConnectionAsync()
    {
        var connection = _connection;
        _connection = null;
        if (connection is null)
        {
            return;
        }

        try
        {
            await connection.DisposeAsync();
        }
        catch (Exception ex)
        {
            AppendLog($"释放连接失败：{ex.Message}");
        }
    }

    private async Task RegisterAsync()
    {
        if (_connection?.State != HubConnectionState.Connected)
        {
            return;
        }

        await _connection.InvokeAsync(
            "Register",
            new AgentRegistration(
                _settings.DeviceId,
                _settings.DeviceName,
                "16.6.0",
                Protocol.CurrentVersion));
    }

    private async Task SendHeartbeatAsync(bool retryRegistration = true, bool applyManualOverride = true)
    {
        if (_connection?.State != HubConnectionState.Connected)
        {
            return;
        }

        try
        {
            if (applyManualOverride)
            {
                ApplyLocalManualOverride(sendHeartbeatForBlackOverlay: false);
            }
            await _connection.InvokeAsync(
                "Heartbeat",
                new AgentHeartbeat(
                    _settings.DeviceId,
                    DateTimeOffset.UtcNow,
                    _state,
                    _stateVerified,
                    _stateDetail));
        }
        catch (Exception ex) when (retryRegistration && !_manualDisconnect)
        {
            AppendLog($"心跳被拒绝，重新注册后重试：{ex.Message}");
            await RegisterAsync();
            await SendHeartbeatAsync(retryRegistration: false, applyManualOverride);
        }
        catch (Exception ex)
        {
            AppendLog($"心跳失败：{ex.Message}");
        }
    }

    private async Task CheckLocalOverrideAndReportAsync()
    {
        if (_connection?.State != HubConnectionState.Connected)
        {
            return;
        }

        if (ApplyLocalManualOverride(sendHeartbeatForBlackOverlay: false))
        {
            await SendHeartbeatAsync(applyManualOverride: false);
        }
    }

    private async Task ExecuteCommandAsync(DisplayCommand command)
    {
        if (!string.Equals(command.DeviceId, _settings.DeviceId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await ReportCommandAsync(command, CommandStage.Accepted, true, "Agent 已接收命令，进入本机执行队列。");
        await _commandLock.WaitAsync();
        try
        {
            _lastRemoteCommandAtUtc = DateTimeOffset.UtcNow;
            AppendLog($"开始执行命令：{command.Kind}。");
            if (_settings.SimulationMode)
            {
                var simulatedState = SimulatedStateFor(command.Kind);
                SetObservedState(simulatedState, verified: true, $"模拟模式已确认 {command.Kind}。");
                await ReportCommandAsync(command, CommandStage.Executed, true, $"模拟执行完成：{command.Kind}。");
                await ReportCommandAsync(command, CommandStage.Verified, true, $"模拟验证完成：{command.Kind}。");
                await SendHeartbeatAsync();
                return;
            }

            switch (command.Kind)
            {
                case CommandKind.TurnOn:
                    var overlayClosedForPowerOn = HideBlackOverlay(updateState: false);
                    // 只做一次显示器唤醒请求，不长期持有 ES_DISPLAY_REQUIRED。
                    // 这样现场人员后续手动息屏或切换显示器时，不会被 Agent 持续压制。
                    SetThreadExecutionState(EsSystemRequired | EsDisplayRequired);
                    if (!await SetMonitorPowerAsync(on: true))
                    {
                        throw new InvalidOperationException("Windows 未接受亮屏电源指令。");
                    }
                    SetExecutionState(active: true);

                    var powerOnDetail = overlayClosedForPowerOn
                        ? "亮屏命令已执行，网页已即时刷新；现场人工操作优先。"
                        : "亮屏命令已执行，黑幕窗口正在解除；现场人工操作优先。";
                    SetObservedState(DisplayState.On, verified: false, powerOnDetail);
                    await ReportCommandAsync(command, CommandStage.Executed, true, powerOnDetail);
                    break;

                case CommandKind.Standby:
                    var overlayClosedForStandby = HideBlackOverlay(updateState: false);
                    // 休眠也是一次性命令；保留系统运行需求，释放显示器亮屏需求。
                    SetExecutionState(active: true);
                    if (_settings.UseMonitorStandby && !await SetMonitorPowerAsync(on: false))
                    {
                        throw new InvalidOperationException("Windows 未接受息屏/待机指令。");
                    }

                    var standbyDetail = overlayClosedForStandby
                        ? "休眠命令已执行，网页已即时刷新；现场唤醒后 Agent 不再继续压制。"
                        : "休眠命令已执行，黑幕窗口正在解除；现场唤醒后 Agent 不再继续压制。";
                    SetObservedState(DisplayState.Standby, verified: false, standbyDetail);
                    await ReportCommandAsync(command, CommandStage.Executed, true, standbyDetail);
                    break;

                case CommandKind.ShowBlackOverlay:
                    if (!_settings.UseBlackOverlay)
                    {
                        throw new InvalidOperationException("当前配置已禁用黑幕遮罩。");
                    }

                    ShowBlackOverlay(updateState: false);
                    await Task.Delay(300);
                    var blackoutWindowVisible = VerifyOverlayWindowPresence();
                    var blackoutPixelsBlack = blackoutWindowVisible && ScreensSampleAsBlack();
                    var blackoutDetail = blackoutWindowVisible
                        ? (blackoutPixelsBlack
                            ? "黑幕遮罩已通过窗口与像素确认。"
                            : "黑幕遮罩窗口已打开；像素采样未完全确认。")
                        : "黑幕遮罩窗口未能打开。";
                    SetObservedState(
                        DisplayState.BlackOverlay,
                        blackoutWindowVisible,
                        blackoutDetail);
                    await ReportCommandAsync(command, CommandStage.Executed, blackoutWindowVisible, blackoutDetail);
                    await ReportCommandAsync(
                        command,
                        blackoutWindowVisible ? CommandStage.Verified : CommandStage.Failed,
                        blackoutWindowVisible,
                        _stateDetail ?? "黑幕状态确认完成。");
                    break;

                case CommandKind.RemoveBlackOverlay:
                    var closed = HideBlackOverlay(updateState: false);
                    SetObservedState(DisplayState.On, closed, closed ? "黑幕遮罩已解除。" : "黑幕遮罩关闭确认失败。");
                    await ReportCommandAsync(command, CommandStage.Executed, closed, "已执行解除黑幕。");
                    await ReportCommandAsync(command, closed ? CommandStage.Verified : CommandStage.Failed, closed, _stateDetail ?? "解除黑幕完成。");
                    break;

                default:
                    throw new InvalidOperationException($"Unsupported command: {command.Kind}");
            }

            await SendHeartbeatAsync();
        }
        catch (Exception ex)
        {
            SetObservedState(DisplayState.Unknown, verified: false, ex.Message);
            await ReportCommandAsync(command, CommandStage.Failed, false, ex.Message);
            await SendHeartbeatAsync();
            AppendLog($"命令失败：{ex.Message}");
        }
        finally
        {
            _commandLock.Release();
        }
    }

    private async Task ReportCommandAsync(
        DisplayCommand command,
        CommandStage stage,
        bool succeeded,
        string detail,
        bool retryRegistration = true)
    {
        if (_connection?.State != HubConnectionState.Connected)
        {
            return;
        }

        try
        {
            await _connection.InvokeAsync(
                "ReportCommand",
                new CommandReceipt(
                    command.CommandId,
                    _settings.DeviceId,
                    stage,
                    DateTimeOffset.UtcNow,
                    succeeded,
                    detail));
        }
        catch (Exception ex) when (retryRegistration && !_manualDisconnect)
        {
            AppendLog($"命令回执被拒绝，重新注册后重试：{ex.Message}");
            await RegisterAsync();
            await ReportCommandAsync(command, stage, succeeded, detail, retryRegistration: false);
        }
        catch (Exception ex)
        {
            AppendLog($"命令回执失败：{ex.Message}");
        }
    }

    private void ShowBlackOverlay(bool updateState = true)
    {
        if (_overlays.Count == 0)
        {
            foreach (var screen in Screen.AllScreens)
            {
                var overlay = new BlackOverlayForm(screen);
                overlay.EmergencyReleaseRequested += (_, _) => ReleaseBlackOverlayByLocalInput();
                _overlays.Add(overlay);
                overlay.Show();
            }
        }

        if (updateState)
        {
            SetObservedState(DisplayState.BlackOverlay, VerifyOverlayWindowPresence(), "本机黑幕遮罩已开启。");
        }

        AppendLog("本机黑幕遮罩已开启。");
    }

    private bool HideBlackOverlay(bool updateState = true)
    {
        _closingBlackOverlay = true;
        try
        {
            foreach (var overlay in _overlays.ToArray())
            {
                try
                {
                    if (!overlay.IsDisposed)
                    {
                        overlay.Close();
                    }

                    overlay.Dispose();
                }
                catch (Exception ex)
                {
                    AppendLog($"关闭黑幕窗口失败：{ex.Message}");
                }
            }
        }
        finally
        {
            _closingBlackOverlay = false;
        }

        _overlays.Clear();
        if (updateState)
        {
            SetObservedState(DisplayState.On, verified: false, "黑幕已解除，亮屏状态需要系统电源确认。");
        }

        AppendLog("本机黑幕遮罩已解除。");
        return VerifyOverlaysClosed();
    }

    private bool ReleaseBlackOverlayByLocalInput(bool sendHeartbeat = true)
    {
        if (_closingBlackOverlay ||
            (_state != DisplayState.BlackOverlay && _overlays.Count == 0))
        {
            return false;
        }

        _lastLocalOverrideAtUtc = DateTimeOffset.UtcNow;
        HideBlackOverlay(updateState: false);
        SetObservedState(DisplayState.On, verified: false, "检测到现场人工操作，已解除黑幕并尊重本机控制。");
        if (sendHeartbeat)
        {
            _ = SendHeartbeatAsync(applyManualOverride: false);
        }

        return true;
    }

    private void SetObservedState(DisplayState state, bool verified, string detail)
    {
        _state = state;
        _stateVerified = verified;
        _stateDetail = detail;
        AppendLog(detail);
    }

    private bool ApplyLocalManualOverride(bool sendHeartbeatForBlackOverlay = true)
    {
        if (!TryGetLastInputUtc(out var lastInputUtc))
        {
            return false;
        }

        if (lastInputUtc <= _lastRemoteCommandAtUtc.AddSeconds(1) ||
            lastInputUtc <= _lastLocalOverrideAtUtc)
        {
            return false;
        }

        if (_state == DisplayState.Standby)
        {
            _lastLocalOverrideAtUtc = lastInputUtc;
            SetThreadExecutionState(EsSystemRequired | EsDisplayRequired);
            SetObservedState(DisplayState.On, verified: false, "检测到现场人工唤醒，已尊重本机控制，不再继续休眠。");
            return true;
        }

        if (_state == DisplayState.BlackOverlay && _overlays.Count > 0)
        {
            return ReleaseBlackOverlayByLocalInput(sendHeartbeatForBlackOverlay);
        }

        return false;
    }

    private async Task<bool> VerifyBlackOverlayAsync(TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow <= deadline)
        {
            await Task.Delay(250);
            if (VerifyOverlayWindowPresence() && ScreensSampleAsBlack())
            {
                return true;
            }
        }

        return false;
    }

    private bool VerifyOverlayWindowPresence() =>
        _overlays.Count == Screen.AllScreens.Length &&
        _overlays.All(overlay => !overlay.IsDisposed && overlay.Visible);

    private bool VerifyOverlaysClosed() =>
        _overlays.Count == 0 || _overlays.All(overlay => overlay.IsDisposed || !overlay.Visible);

    private bool ScreensSampleAsBlack()
    {
        foreach (var screen in Screen.AllScreens)
        {
            var bounds = screen.Bounds;
            var offsetX = Math.Max(1, Math.Min(40, bounds.Width / 4));
            var offsetY = Math.Max(1, Math.Min(40, bounds.Height / 4));
            var points = new[]
            {
                new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2),
                new Point(bounds.Left + offsetX, bounds.Top + offsetY),
                new Point(bounds.Right - offsetX, bounds.Top + offsetY),
                new Point(bounds.Left + offsetX, bounds.Bottom - offsetY),
                new Point(bounds.Right - offsetX, bounds.Bottom - offsetY)
            };

            foreach (var point in points)
            {
                if (!IsBlackPixel(point))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private bool IsBlackPixel(Point point)
    {
        try
        {
            using var bitmap = new Bitmap(1, 1);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(point, Point.Empty, new Size(1, 1));
            var color = bitmap.GetPixel(0, 0);
            return Math.Max(color.R, Math.Max(color.G, color.B)) <= _settings.BlackPixelThreshold;
        }
        catch
        {
            return false;
        }
    }

    private static DisplayState SimulatedStateFor(CommandKind kind) => kind switch
    {
        CommandKind.Standby => DisplayState.Standby,
        CommandKind.ShowBlackOverlay => DisplayState.BlackOverlay,
        _ => DisplayState.On
    };

    private void SetExecutionState(bool active)
    {
        if (active && _settings.PreventSystemSleep)
        {
            SetThreadExecutionState(EsContinuous | EsSystemRequired);
        }
        else
        {
            SetThreadExecutionState(EsContinuous);
        }
    }

    private void OnUi(Action action)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(action);
            return;
        }

        action();
    }

    private void AppendLog(string message)
    {
        OnUi(() =>
        {
            var line = $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";
            _log.AppendText(line);
        });
    }

    private static Task<bool> SetMonitorPowerAsync(bool on) =>
        Task.Run(() => SetMonitorPower(on));

    private static bool SetMonitorPower(bool on) =>
        SendNotifyMessage(
            HwndBroadcast,
            WmSysCommand,
            (nint)ScMonitorPower,
            on ? -1 : 2) ||
        SendMessageTimeout(
            HwndBroadcast,
            WmSysCommand,
            (nint)ScMonitorPower,
            on ? -1 : 2,
            SmtoAbortIfHung,
            1500,
            out _);

    private static bool TryGetLastInputUtc(out DateTimeOffset lastInputUtc)
    {
        var info = new LastInputInfo
        {
            CbSize = (uint)Marshal.SizeOf<LastInputInfo>()
        };
        if (!GetLastInputInfo(ref info))
        {
            lastInputUtc = DateTimeOffset.MinValue;
            return false;
        }

        var currentTick = unchecked((uint)GetTickCount64());
        var idleMilliseconds = unchecked(currentTick - info.DwTime);
        lastInputUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(idleMilliseconds);
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint CbSize;
        public uint DwTime;
    }

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SendMessageTimeout(
        nint hWnd,
        uint message,
        nint wParam,
        nint lParam,
        uint flags,
        uint timeout,
        out nint result);

    [LibraryImport("user32.dll", EntryPoint = "SendNotifyMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SendNotifyMessage(
        nint hWnd,
        uint message,
        nint wParam,
        nint lParam);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint SetThreadExecutionState(uint esFlags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLastInputInfo(ref LastInputInfo info);

    [LibraryImport("kernel32.dll")]
    private static partial ulong GetTickCount64();

    private sealed class ForeverRetryPolicy : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext retryContext)
        {
            var seconds = retryContext.PreviousRetryCount switch
            {
                0 => 0,
                1 => 2,
                2 => 5,
                3 => 10,
                _ => 30
            };
            return TimeSpan.FromSeconds(seconds);
        }
    }
}
