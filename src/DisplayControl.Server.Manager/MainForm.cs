using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;

namespace DisplayControl.Server.Manager;

public sealed class MainForm : Form
{
    private const string ServiceName = "DisplayControlCenterV11";
    // The same semantic palette is used by the web console and Windows Agent.
    private static readonly Color AppBackground = Color.FromArgb(245, 248, 253);
    private static readonly Color Surface = Color.White;
    private static readonly Color PrimaryText = Color.FromArgb(15, 23, 42);
    private static readonly Color SecondaryText = Color.FromArgb(100, 116, 139);
    private static readonly Color Primary = Color.FromArgb(37, 99, 235);
    private static readonly Color PrimaryHover = Color.FromArgb(29, 78, 216);
    private static readonly Color Accent = Color.FromArgb(3, 105, 161);
    private static readonly Color Danger = Color.FromArgb(220, 38, 38);
    private readonly Label _serviceStatus = new();
    private readonly Label _healthStatus = new();
    private readonly Label _credentialStatus = new();
    private readonly Button _openCredentialFile = new();
    private readonly TextBox _addresses = new();
    private readonly TextBox _log = new();
    private readonly System.Windows.Forms.Timer _timer = new();

    public MainForm()
    {
        BuildUi();
        _timer.Interval = 5000;
        _timer.Tick += async (_, _) => await RefreshStatusAsync();
        _timer.Start();
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        await RefreshStatusAsync();
    }

    private void BuildUi()
    {
        Text = "展示屏控制服务器管理器";
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        DoubleBuffered = true;
        ClientSize = new Size(1160, 900);
        MinimumSize = new Size(1080, 840);
        AutoScroll = true;
        AutoScrollMinSize = new Size(1040, 810);
        BackColor = AppBackground;
        Padding = new Padding(20);
        Font = new Font("Microsoft YaHei UI", 9.6F, FontStyle.Regular, GraphicsUnit.Point);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            BackColor = AppBackground
        };
        // 关键区域使用明确高度，避免高 DPI 或小窗口下标题、地址和按钮被挤压裁切；
        // 运行日志使用剩余空间随窗口伸缩。
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 118));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 118));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 168));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 220));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        var headerCard = MakeCard(new Padding(24, 16, 24, 14));
        headerCard.Margin = new Padding(0, 0, 0, 14);
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 1,
            BackColor = Surface
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var titleBlock = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Surface
        };
        titleBlock.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        titleBlock.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        titleBlock.Controls.Add(new Label
        {
            Text = "展示屏控制服务器",
            Dock = DockStyle.Fill,
            Font = new Font("Microsoft YaHei UI", 18.5F, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = PrimaryText,
            TextAlign = ContentAlignment.BottomLeft
        }, 0, 0);
        titleBlock.Controls.Add(new Label
        {
            Text = "Server 11.7.0 · Windows 服务管理器",
            Dock = DockStyle.Fill,
            ForeColor = SecondaryText,
            TextAlign = ContentAlignment.TopLeft
        }, 0, 1);
        header.Controls.Add(titleBlock, 0, 0);
        headerCard.Controls.Add(header);
        root.Controls.Add(headerCard, 0, 0);

        var statusPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            BackColor = AppBackground,
            Margin = new Padding(0, 0, 0, 14)
        };
        statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        statusPanel.Controls.Add(MakeStatusBox("服务状态", _serviceStatus, new Padding(0, 0, 6, 0)), 0, 0);
        statusPanel.Controls.Add(MakeStatusBox("健康检查", _healthStatus, new Padding(6, 0, 0, 0)), 1, 0);
        root.Controls.Add(statusPanel, 0, 1);

        var addressCard = MakeCard(new Padding(22, 16, 22, 16));
        addressCard.Margin = new Padding(0, 0, 0, 14);
        var addressLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Surface
        };
        addressLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        addressLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        addressLayout.Controls.Add(MakeSectionTitle("访问地址"), 0, 0);
        _addresses.Multiline = true;
        _addresses.ReadOnly = true;
        _addresses.Dock = DockStyle.Fill;
        _addresses.BorderStyle = BorderStyle.FixedSingle;
        _addresses.BackColor = Surface;
        _addresses.ForeColor = Accent;
        _addresses.Font = new Font("Microsoft YaHei UI", 9.4F, FontStyle.Regular, GraphicsUnit.Point);
        _addresses.ScrollBars = ScrollBars.Both;
        _addresses.WordWrap = false;
        _addresses.HideSelection = true;
        _addresses.TabStop = false;
        _addresses.AccessibleName = "服务器访问地址";
        var addressBody = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Surface
        };
        addressBody.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        addressBody.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 320));
        addressBody.Controls.Add(_addresses, 0, 0);
        var credentialPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(16, 0, 0, 0),
            BackColor = Surface
        };
        credentialPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        credentialPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _credentialStatus.Text = "正在检查初始密码…";
        _credentialStatus.Dock = DockStyle.Fill;
        _credentialStatus.ForeColor = SecondaryText;
        _credentialStatus.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold, GraphicsUnit.Point);
        _credentialStatus.TextAlign = ContentAlignment.MiddleLeft;
        _credentialStatus.AccessibleName = "初始密码状态";
        credentialPanel.Controls.Add(_credentialStatus, 0, 0);
        ConfigureButton(_openCredentialFile, "打开初始密码文件", ButtonKind.Accent);
        _openCredentialFile.MinimumSize = new Size(0, 52);
        _openCredentialFile.Click += async (_, _) =>
        {
            _openCredentialFile.Enabled = false;
            try
            {
                await OpenInitialCredentials();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "初始密码", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                UpdateCredentialState();
            }
        };
        credentialPanel.Controls.Add(_openCredentialFile, 0, 1);
        addressBody.Controls.Add(credentialPanel, 1, 0);
        addressLayout.Controls.Add(addressBody, 0, 1);
        addressCard.Controls.Add(addressLayout);
        root.Controls.Add(addressCard, 0, 2);

        var actionCard = MakeCard(new Padding(22, 16, 22, 18));
        actionCard.Margin = new Padding(0, 0, 0, 14);
        var actionLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Surface
        };
        actionLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        actionLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        actionLayout.Controls.Add(MakeSectionTitle("服务操作"), 0, 0);
        var actionGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 3,
            BackColor = Surface
        };
        for (var column = 0; column < 4; column++)
        {
            actionGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        }
        actionGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        actionGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        actionGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        actionGrid.Controls.Add(MakeButton("安装 / 更新服务", () => RunScript("install-service-current-folder.ps1"), ButtonKind.Primary), 0, 0);
        actionGrid.Controls.Add(MakeButton("启动服务", StartService, ButtonKind.Accent), 1, 0);
        actionGrid.Controls.Add(MakeButton("停止服务", StopService), 2, 0);
        actionGrid.Controls.Add(MakeButton("打开控制网页", OpenWeb, ButtonKind.Accent), 3, 0);
        actionGrid.Controls.Add(MakeButton("复制局域网地址", CopyLanAddress), 0, 1);
        actionGrid.Controls.Add(MakeButton("刷新状态", RefreshStatusAsync), 1, 1);
        actionGrid.Controls.Add(MakeButton("重置管理员密码", ResetAdminPassword, ButtonKind.Danger), 2, 1);
        actionGrid.Controls.Add(MakeButton("卸载服务", () => RunScript("uninstall-service-current-folder.ps1"), ButtonKind.Danger), 3, 1);
        actionGrid.Controls.Add(MakeButton("选择安装位置", ChooseInstallLocation, ButtonKind.Accent), 0, 2);
        actionLayout.Controls.Add(actionGrid, 0, 1);
        actionCard.Controls.Add(actionLayout);
        root.Controls.Add(actionCard, 0, 3);

        var logCard = MakeCard(new Padding(20, 12, 20, 16));
        var logLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Surface
        };
        logLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        logLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        logLayout.Controls.Add(MakeSectionTitle("运行日志"), 0, 0);
        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.Dock = DockStyle.Fill;
        _log.BorderStyle = BorderStyle.FixedSingle;
        _log.BackColor = AppBackground;
        _log.ForeColor = Color.FromArgb(51, 65, 85);
        _log.Font = new Font("Consolas", 9.5F, FontStyle.Regular, GraphicsUnit.Point);
        _log.AccessibleName = "服务器运行日志";
        logLayout.Controls.Add(_log, 0, 1);
        logCard.Controls.Add(logLayout);
        root.Controls.Add(logCard, 0, 4);
    }

    private enum ButtonKind
    {
        Secondary,
        Primary,
        Accent,
        Danger
    }

    private static Panel MakeCard(Padding padding) =>
        new()
        {
            Dock = DockStyle.Fill,
            Padding = padding,
            BackColor = Surface,
            BorderStyle = BorderStyle.FixedSingle
        };

    private static Label MakeSectionTitle(string text) =>
        new()
        {
            Text = text,
            Dock = DockStyle.Fill,
            ForeColor = PrimaryText,
            Font = new Font("Microsoft YaHei UI", 10.5F, FontStyle.Bold, GraphicsUnit.Point),
            TextAlign = ContentAlignment.MiddleLeft
        };

    private static Panel MakeStatusBox(
        string title,
        Label value,
        Padding margin)
    {
        var card = MakeCard(new Padding(18, 16, 18, 14));
        card.Margin = margin;
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            BackColor = Surface
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            ForeColor = SecondaryText,
            Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold, GraphicsUnit.Point),
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);
        value.Text = "检查中…";
        value.Dock = DockStyle.Fill;
        value.ForeColor = PrimaryText;
        value.Font = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold, GraphicsUnit.Point);
        value.TextAlign = ContentAlignment.MiddleLeft;
        value.AutoEllipsis = true;
        value.AccessibleName = title;
        panel.Controls.Add(value, 0, 1);
        card.Controls.Add(panel);
        return card;
    }

    private static Button MakeButton(
        string text,
        Func<Task> action,
        ButtonKind kind = ButtonKind.Secondary)
    {
        var button = new Button();
        ConfigureButton(button, text, kind);
        button.Click += async (_, _) =>
        {
            var originalText = button.Text;
            button.Enabled = false;
            button.Text = "处理中…";
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                button.Text = originalText;
                button.Enabled = true;
            }
        };
        return button;
    }

    private static void ConfigureButton(Button button, string text, ButtonKind kind)
    {
        button.Text = text;
        button.Dock = DockStyle.Fill;
        button.MinimumSize = new Size(0, 44);
        button.Margin = new Padding(4);
        button.FlatStyle = FlatStyle.Flat;
        button.Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold, GraphicsUnit.Point);
        button.UseVisualStyleBackColor = false;
        button.AccessibleName = text;
        StyleButton(button, kind);
    }

    private static void StyleButton(Button button, ButtonKind kind)
    {
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
            case ButtonKind.Danger:
                button.BackColor = Surface;
                button.ForeColor = Danger;
                button.FlatAppearance.BorderColor = Color.FromArgb(254, 202, 202);
                button.MouseEnter += (_, _) => button.BackColor = Color.FromArgb(254, 242, 242);
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

    private static string AppRoot => AppContext.BaseDirectory.TrimEnd('\\');

    private static string PackageRoot
    {
        get
        {
            var appDirectory = new DirectoryInfo(AppRoot);
            return string.Equals(appDirectory.Name, "app", StringComparison.OrdinalIgnoreCase) &&
                appDirectory.Parent is not null
                ? appDirectory.Parent.FullName
                : appDirectory.FullName;
        }
    }

    private static string ToolsRoot => Path.Combine(PackageRoot, "tools");

    private async Task RefreshStatusAsync()
    {
        _serviceStatus.Text = GetServiceStatus();
        _healthStatus.Text = await CheckHealthAsync();
        ApplyStatusColors();
        _addresses.Text = string.Join(
            Environment.NewLine,
            "本机网页：http://127.0.0.1:8080",
            "局域网网页：" + string.Join("  ", GetLocalAddresses().Select(ip => $"http://{ip}:8080")));
        UpdateCredentialState();
    }

    private void ApplyStatusColors()
    {
        _serviceStatus.ForeColor = string.Equals(_serviceStatus.Text, "Running", StringComparison.OrdinalIgnoreCase)
            ? Color.FromArgb(21, 128, 61)
            : string.Equals(_serviceStatus.Text, "未安装", StringComparison.Ordinal)
                ? SecondaryText
                : Color.FromArgb(180, 83, 9);
        _healthStatus.ForeColor = _healthStatus.Text.StartsWith("Healthy", StringComparison.OrdinalIgnoreCase)
            ? Color.FromArgb(21, 128, 61)
            : Danger;
    }

    private static string GetServiceStatus()
    {
        try
        {
            using var service = new ServiceController(ServiceName);
            return service.Status.ToString();
        }
        catch
        {
            return "未安装";
        }
    }

    private static async Task<string> CheckHealthAsync()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        try
        {
            var text = await client.GetStringAsync("http://127.0.0.1:8080/health");
            if (text.TrimStart().StartsWith('<'))
            {
                return "端口返回旧网页";
            }

            if (!text.Contains("Healthy", StringComparison.OrdinalIgnoreCase))
            {
                return text.Length <= 36 ? text : text[..36] + "…";
            }

            var versionJson = await client.GetStringAsync("http://127.0.0.1:8080/version");
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(versionJson);
            }
            catch (JsonException)
            {
                return "端口被旧版本占用";
            }

            using (document)
            {
                var version = document.RootElement.TryGetProperty("serverVersion", out var versionElement)
                    ? versionElement.GetString()
                    : null;
                return version?.StartsWith("11.", StringComparison.OrdinalIgnoreCase) == true
                    ? $"Healthy V{version}"
                    : $"端口被其他版本占用：V{version ?? "未知"}";
            }
        }
        catch (Exception ex)
        {
            return "不可用：" + ex.Message;
        }
    }

    private Task RunScript(string scriptName)
    {
        var script = Path.Combine(ToolsRoot, scriptName);
        if (!File.Exists(script))
        {
            script = Path.Combine(PackageRoot, scriptName);
        }
        if (!File.Exists(script))
        {
            throw new FileNotFoundException("管理脚本不存在", script);
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"",
            WorkingDirectory = PackageRoot,
            UseShellExecute = true
        });
        AppendLog("已启动脚本：" + scriptName);
        return Task.CompletedTask;
    }

    private async Task StartService()
    {
        await RunPowerShellAsync("Start-Service DisplayControlCenterV11");
        await RefreshStatusAsync();
    }

    private async Task StopService()
    {
        await RunPowerShellAsync("Stop-Service DisplayControlCenterV11");
        await RefreshStatusAsync();
    }

    private Task OpenWeb()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "http://127.0.0.1:8080",
            UseShellExecute = true
        });
        return Task.CompletedTask;
    }

    private Task CopyLanAddress()
    {
        var address = GetLocalAddresses().FirstOrDefault() ?? "127.0.0.1";
        Clipboard.SetText($"http://{address}:8080");
        AppendLog("已复制局域网地址。");
        return Task.CompletedTask;
    }

    private Task ChooseInstallLocation()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "请选择 Server 安装位置。程序会在所选目录下创建当前 Server 文件夹。",
            ShowNewFolderButton = true,
            SelectedPath = Directory.Exists(@"A:\") ? @"A:\" : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return Task.CompletedTask;
        }

        var sourceRoot = PackageRoot.TrimEnd('\\');
        var packageName = Path.GetFileName(sourceRoot);
        var destinationRoot = Path.GetFullPath(dialog.SelectedPath).TrimEnd('\\');
        var destination = Path.GetFullPath(Path.Combine(destinationRoot, packageName)).TrimEnd('\\');
        if (destination.Equals(sourceRoot, StringComparison.OrdinalIgnoreCase) ||
            destination.StartsWith(sourceRoot + "\\", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("安装位置不能选择当前 Server 文件夹或其子文件夹。");
        }

        if (Directory.Exists(destination) &&
            Directory.EnumerateFileSystemEntries(destination).Any())
        {
            throw new InvalidOperationException($"目标文件夹已存在且不为空：{destination}");
        }

        CopyDirectory(sourceRoot, destination);
        AppendLog($"Server 已复制到新安装位置：{destination}");
        MessageBox.Show(
            $"Server 已复制到：\r\n{destination}\r\n\r\n请到新位置打开服务器管理器；如果当前服务已安装，请在新位置点击“安装 / 更新服务”。",
            "展示屏控制服务器",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{destination}\"",
            UseShellExecute = true
        });
        return Task.CompletedTask;
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

    private Task OpenInitialCredentials()
    {
        var credentialFile = Path.Combine(PackageRoot, "data", "initial-admin-credentials.txt");
        if (!File.Exists(credentialFile))
        {
            throw new FileNotFoundException(
                "尚未生成初始密码文件。请先安装并启动服务，或使用“重置管理员密码”。",
                credentialFile);
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = credentialFile,
            UseShellExecute = true
        });
        AppendLog("已打开初始管理员密码文件。");
        return Task.CompletedTask;
    }

    private void UpdateCredentialState()
    {
        var credentialFile = Path.Combine(PackageRoot, "data", "initial-admin-credentials.txt");
        var exists = File.Exists(credentialFile);
        _credentialStatus.Text = exists ? "初始密码已生成" : "暂无初始密码文件";
        _credentialStatus.ForeColor = exists ? Color.FromArgb(21, 128, 61) : SecondaryText;
        _openCredentialFile.Enabled = exists;
    }

    private async Task ResetAdminPassword()
    {
        if (MessageBox.Show(
            "确认重置网页管理员密码？设备和日志会保留，现有网页登录会失效。",
            "重置管理员密码",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }

        await StopService();
        foreach (var file in new[]
        {
            Path.Combine(PackageRoot, "data", "admin-credentials.json"),
            Path.Combine(PackageRoot, "data", "initial-admin-credentials.txt")
        })
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }

        await StartService();
        var credentialFile = Path.Combine(PackageRoot, "data", "initial-admin-credentials.txt");
        UpdateCredentialState();
        AppendLog(File.Exists(credentialFile)
            ? "管理员密码已重置：" + credentialFile
            : "服务已启动，请稍后检查 data 目录中的初始凭据文件。");
    }

    private async Task RunPowerShellAsync(string command)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(command)),
            WorkingDirectory = PackageRoot,
            UseShellExecute = true,
            Verb = "runas"
        };
        Process.Start(psi);
        AppendLog("已请求管理员执行：" + command);
        await Task.Delay(1200);
    }

    private static IEnumerable<string> GetLocalAddresses()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic => nic.OperationalStatus == OperationalStatus.Up)
            .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
            .Where(info => info.Address.AddressFamily == AddressFamily.InterNetwork &&
                !IPAddress.IsLoopback(info.Address))
            .Select(info => info.Address.ToString())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(ip => ip, StringComparer.OrdinalIgnoreCase);
    }

    private void AppendLog(string message)
    {
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
    }
}
