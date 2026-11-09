using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ClassBox.Abstractions.Plugins;
using ClassBox.Abstractions.Services;
using ClassBox.Plugins.ClassTalk.Crypto;
using ClassBox.Plugins.ClassTalk.Net;

namespace ClassBox.Plugins.ClassTalk.Views;

/// <summary>
/// ClassTalk 插件页面：原 MainWindow 的登录 + 聊天视图改为 UserControl，
/// 原 AdminWindow 改为页面内浮层控制台；置顶/悬浮球/关闭确认由宿主负责。
/// </summary>
public partial class ClassTalkView : UserControl
{
    private static readonly byte[] FixedSalt = Encoding.UTF8.GetBytes("ClassTalk-Salt01");

    /// <summary>字体回退链：中文 → 拉丁 → 阿拉伯/希伯来 → 印地/泰/韩 → emoji，覆盖各类 Unicode 字符。</summary>
    private static readonly FontFamily UiFont =
        new("Microsoft YaHei UI, Segoe UI, Tahoma, Arial, Nirmala UI, Malgun Gothic, Leelawadee UI, Segoe UI Emoji");

    private readonly ClassTalkPlugin _plugin;
    private readonly StringBuilder _adminOutput = new();

    private ChatServer? _server;
    private ChatClient? _client;
    private Lrnc1024Session? _session;
    private bool _connected;
    private bool _closed;
    private string _lanIp = "127.0.0.1";
    private string _roomEndpoint = "";
    private bool _emptyRoomHinted;
    private int _hostPort;

    // ===== 管理员状态（隐藏机制，界面上不出现任何管理员入口/文案） =====
    private string? _adminNick;
    private IReadOnlyList<string> _members = Array.Empty<string>();

    // ===== 口令与局域网会话检测 =====
    private const string AdminCode = "/admin";
    private string _roomPass = "";
    private SessionAnnouncer? _announcer;
    private SessionScanner? _scanner;
    private System.Windows.Threading.DispatcherTimer? _scanTimer;
    private IReadOnlyList<LanSession> _sessions = Array.Empty<LanSession>();
    private readonly string _instanceId = Guid.NewGuid().ToString("N").Substring(0, 12);

    public ClassTalkView(ClassTalkPlugin plugin)
    {
        _plugin = plugin;
        InitializeComponent();

        HostBox.IsEnabled = false;
        HostBox.Opacity = 0.45;

        // 关键：主机对外公布的必须是真实内网 IPv4，127.0.0.1 只在本机内有效
        RefreshLanIp();
        HostBox.Text = _lanIp;
        NickBox.Text = Environment.UserName;

        StartDiscovery();
    }

    private IPluginContext Host => _plugin.Host;

    // ===================== 基础工具 =====================
    private void Ui(Action action)
    {
        if (_closed) return;
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        try
        {
            if (Dispatcher.CheckAccess()) action();
            else Dispatcher.BeginInvoke(action);
        }
        catch
        {
            // 页面已卸载，忽略
        }
    }

    private Brush Res(string key, Color fallback)
        => TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

    private void Notify(string message, string title = "ClassTalk", bool urgent = false)
    {
        try
        {
            Host.Alerts.Show(new SideAlertRequest
            {
                Title = title,
                Message = message,
                Glyph = "\uE8BD",
                AutoCloseSeconds = 6,
                IsUrgent = urgent
            });
        }
        catch (Exception ex)
        {
            _plugin.LogError("侧边提示发送失败", ex);
        }
    }

    private void ShowError(string text)
    {
        LoginError.Text = text;
        LoginError.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    // ===================== 局域网 IP =====================
    private void RefreshLanIp()
    {
        try { _lanIp = LocalNetwork.GetLanIpv4(); }
        catch { _lanIp = "127.0.0.1"; }
        LanIpText.Text = _lanIp;
    }

    private void RefreshIp_Click(object sender, RoutedEventArgs e)
    {
        string before = _lanIp;
        RefreshLanIp();
        if (_server == null) HostBox.Text = _lanIp;

        if (before != _lanIp)
        {
            AppendSystem($"已更新本机局域网 IP：{_lanIp}");
            if (_server != null && _hostPort > 0)
            {
                _roomEndpoint = $"{_lanIp}:{_hostPort}";
                ShowRoomInfo($"房间地址\n{_roomEndpoint}\n（点击复制）");
            }
        }
        else
        {
            AppendSystem($"本机局域网 IP：{_lanIp}");
        }
    }

    // ===================== 局域网会话检测 =====================
    private void StartDiscovery()
    {
        try
        {
            _scanner = new SessionScanner();
            _scanner.SessionsChanged += () => Ui(RefreshSessionList);
            _scanner.Start();
        }
        catch (Exception ex)
        {
            _scanner = null;
            _plugin.LogError("局域网会话扫描启动失败", ex);
            ScanStatusText.Text = "扫描不可用：UDP 55211 被占用或被防火墙拦截";
        }

        // 周期重绘：公告 TTL 到期需要把过期会话从列表移除
        _scanTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _scanTimer.Tick += (_, _) => RefreshSessionList();
        _scanTimer.Start();

        RefreshSessionList();
    }

    private void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        _announcer?.PublishOnce(); // 本机正作为主机时，刷新同时把自己的房间尽快公告出去
        RefreshSessionList();
    }

    private void AutoScan_Click(object sender, RoutedEventArgs e) => RefreshSessionList();

    private LanSession? BuildAnnouncement()
    {
        var server = _server;
        if (server is null || _hostPort <= 0) return null;
        return new LanSession(
            Name: server.RoomName,
            HostNick: server.HostNick,
            HostIp: _lanIp,
            Port: _hostPort,
            Members: server.MemberCount,
            Encrypted: true,
            InstanceId: _instanceId,
            LastSeen: DateTime.Now);
    }

    private void RefreshSessionList()
    {
        if (_closed) return;
        try
        {
            _sessions = _scanner?.Snapshot() ?? Array.Empty<LanSession>();

            DiscoverPanel.Children.Clear();
            foreach (var s in _sessions) DiscoverPanel.Children.Add(BuildSessionCard(s));

            DiscoverEmptyText.Visibility = _sessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            string mode = AutoScanCheck?.IsChecked == true ? "自动刷新" : "手动刷新";
            ScanStatusText.Text = _scanner is null
                ? "扫描不可用（UDP 55211 未就绪）"
                : _sessions.Count == 0 ? $"暂未发现会话 · {mode}" : $"发现 {_sessions.Count} 个会话 · {mode}";
        }
        catch (Exception ex)
        {
            _plugin.LogError("刷新局域网会话列表失败", ex);
        }
    }

    private Border BuildSessionCard(LanSession s)
    {
        bool isSelf = _server is not null && s.Port == _hostPort && s.HostNick == _server.HostNick;

        var card = new Border
        {
            Background = Res("AppLayerAltBrush", Color.FromRgb(0xF1, 0xF2, 0xF5)),
            BorderBrush = Res("AppStrokeBrush", Color.FromRgb(0xDD, 0xDD, 0xDD)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(2, 2, 2, 8)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock
        {
            Text = (isSelf ? "（本机）" : "") + s.Name,
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            FontFamily = UiFont,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Res("AppTextPrimaryBrush", Color.FromRgb(0x1A, 0x1A, 0x1A))
        });
        info.Children.Add(new TextBlock
        {
            Text = $"主机 {s.HostNick} · {s.Endpoint} · 在线 {s.Members} 人 · {(s.Encrypted ? "加密" : "明文")}",
            FontSize = 11,
            Margin = new Thickness(0, 5, 0, 0),
            FontFamily = UiFont,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Res("AppTextSecondaryBrush", Color.FromRgb(0x6B, 0x6B, 0x6B))
        });
        Grid.SetColumn(info, 0);
        grid.Children.Add(info);

        var join = new Button
        {
            Content = isSelf ? "已在本机" : "加入",
            Style = TryFindResource("AccentButtonStyle") as Style,
            MinWidth = 68,
            Height = 30,
            FontSize = 12,
            FontFamily = UiFont,
            IsEnabled = !isSelf,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        };
        var target = s;
        join.Click += (_, _) => JoinSession(target);
        Grid.SetColumn(join, 1);
        grid.Children.Add(join);

        card.Child = grid;
        return card;
    }

    /// <summary>一键加入：按公告里的地址与端口填好登录表单并直接连接（口令沿用本机已填过的口令）。</summary>
    private void JoinSession(LanSession s)
    {
        if (_connected)
        {
            Notify("请先断开当前聊天，再加入其他会话", "ClassTalk");
            return;
        }

        ModeJoin.IsChecked = true;
        HostBox.IsEnabled = true;
        HostBox.Opacity = 1.0;
        HostBox.Text = s.HostIp;
        PortBox.Text = s.Port.ToString();
        if (string.IsNullOrWhiteSpace(NickBox.Text)) NickBox.Text = Environment.UserName;

        if (string.IsNullOrWhiteSpace(PassBox.Password) && _roomPass.Length > 0)
            PassBox.Password = _roomPass;

        if (PassBox.Password.Length > 0)
        {
            AppendSystem($"正在加入会话「{s.Name}」（{s.Endpoint}）");
            ConnectButton_Click(ConnectButton, new RoutedEventArgs());
        }
        else
        {
            ShowError($"已填入会话「{s.Name}」的地址 {s.Endpoint}，请输入该会话的加密口令后点“开始连接”");
            PassBox.Focus();
        }
    }

    // ===================== 登录 =====================
    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        // XAML 加载期 RadioButton 的 IsChecked="True" 会提前触发本事件，此时后续控件尚未创建
        if (ModeJoin == null || HostBox == null) return;
        bool join = ModeJoin.IsChecked == true;
        HostBox.IsEnabled = join;
        HostBox.Opacity = join ? 1.0 : 0.45;
        if (join && string.IsNullOrWhiteSpace(HostBox.Text)) HostBox.Text = _lanIp;
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        ShowError("");
        string nick = NickBox.Text.Trim();
        if (nick.Length == 0) { ShowError("请输入昵称"); return; }
        if (nick.Length > 32) { ShowError("昵称最长 32 字符"); return; }

        string password = PassBox.Password;
        if (password.Length == 0) { ShowError("请输入加密口令"); return; }
        if (password.Length < 6) { ShowError("口令太短，建议至少 6 位（双方必须完全一致）"); }

        if (!int.TryParse(PortBox.Text.Trim(), out int port) || port < 1 || port > 65535)
        {
            ShowError("端口无效（1-65535）");
            return;
        }

        _roomPass = password;
        string roomName = RoomNameBox.Text.Trim();

        ConnectButton.IsEnabled = false;
        ConnectButton.Content = "正在连接…";
        try
        {
            byte[] master = Lrnc1024Session.DeriveMasterKey(password, FixedSalt);
            _session = new Lrnc1024Session(master);

            if (ModeHost.IsChecked == true)
            {
                _server = new ChatServer(nick, _session, port, roomName);
                AttachServerEvents();
                _server.Start();
                _hostPort = port;
                _emptyRoomHinted = false;

                // 启动时重新探测内网 IP，并在界面明确展示，绝不对外显示 127.0.0.1
                RefreshLanIp();
                HostBox.Text = _lanIp;
                _roomEndpoint = $"{_lanIp}:{port}";
                bool fwOk = LocalNetwork.EnsureFirewallRule(port, out string fwMsg);

                // 开始向局域网公告本会话（UDP 55211），其他设备打开页面即可看到并一键加入
                try
                {
                    _announcer = new SessionAnnouncer(BuildAnnouncement);
                    _announcer.Start();
                    _announcer.PublishOnce();
                }
                catch (Exception ex)
                {
                    _plugin.LogError("会话公告广播启动失败", ex);
                }

                EnterChatView(nick, $"聊天室「{_server.RoomName}」已创建，端口 {port}");
                AppendSystem($"其他设备请填写主机 IP：{_lanIp}    端口：{port}");
                AppendSystem(fwMsg);
                if (!fwOk) AppendSystem("提示：当前进程未提权时防火墙规则可能无法添加，可用提权身份运行后放行该端口。");
                ShowRoomInfo($"房间地址\n{_roomEndpoint}\n（点击复制）");

                if (_lanIp == "127.0.0.1")
                    AppendSystem("未检测到局域网 IP：本机可能未接入 Wi-Fi/网线，其他设备将无法连接。");

                RefreshSessionList();
                _plugin.LogInfo($"主机模式启动，端口 {port}，内网地址 {_roomEndpoint}，会话名 {_server.RoomName}");
            }
            else
            {
                string host = HostBox.Text.Trim();
                if (host.Length == 0)
                {
                    ShowError("请输入主机 IP");
                    ResetConnectButton();
                    return;
                }

                _client = new ChatClient();
                AttachClientEvents();
                await ConnectClientAsync(host, port, nick);
            }
        }
        catch (Exception ex)
        {
            ShowError($"连接失败：{ex.Message}");
            ResetConnectButton();
            Cleanup();
        }
    }

    private void ResetConnectButton()
    {
        ConnectButton.IsEnabled = true;
        ConnectButton.Content = "开始连接";
    }

    private async Task ConnectClientAsync(string host, int port, string nick)
    {
        try
        {
            await _client!.ConnectAsync(host, port, nick, _session!);
            Ui(() =>
            {
                _roomEndpoint = $"{host}:{port}";
                _emptyRoomHinted = false;
                EnterChatView(nick, $"已加入 {host}:{port}");
                ShowRoomInfo($"已连接主机\n{_roomEndpoint}");
                UpdateStatus();
            });
            _plugin.LogInfo($"客户端已加入 {host}:{port}");
        }
        catch (Exception ex)
        {
            Ui(() =>
            {
                ShowError($"连接失败：{ex.Message}");
                ResetConnectButton();
                _client?.Dispose();
                _client = null;
                _session = null;
            });
        }
    }

    private void AttachServerEvents()
    {
        _server!.MessageReceived += (s, m) => Ui(() => AppendBubble(s, m, isOwn: false));
        _server.PlainMessageReceived += (s, m) => Ui(() => AppendBubble(s, m, isOwn: false, isPlain: true));
        _server.SystemMessage += t => Ui(() => AppendSystem(t));
        _server.MembersUpdated += list => Ui(() => UpdateMembers(list));
        _server.Stopped += reason => Ui(() => OnDisconnected(reason));
        _server.AdminChanged += nick => Ui(() => OnAdminChanged(nick));
        _server.CommandIssued += (target, cmd) => Ui(() => AppendSystem($"已下发命令给 {target}：{cmd}"));
        _server.CommandOutput += (target, output) => Ui(() =>
        {
            if (AdminLayer.Visibility == Visibility.Visible) AppendOutput(target, output);
            else AppendSystem($"[{target}] 命令输出：{Shorten(output)}");
        });
    }

    private void AttachClientEvents()
    {
        _client!.MessageReceived += (s, m) => Ui(() => AppendBubble(s, m, isOwn: s == _client.Nick));
        _client.PlainMessageReceived += (s, m) => Ui(() => AppendBubble(s, m, isOwn: s == _client.Nick, isPlain: true));
        _client.SystemMessage += t => Ui(() => AppendSystem(t));
        _client.MembersUpdated += list => Ui(() => UpdateMembers(list));
        _client.Disconnected += reason => Ui(() => OnDisconnected(reason));
        _client.AdminResult += (granted, holder) => Ui(() => OnAdminResult(granted, holder));
        _client.AdminChanged += nick => Ui(() => OnAdminChanged(nick));
        _client.CommandReceived += (admin, cmd) => Ui(() => _ = RunRemoteCommandAsync(admin, cmd));
        _client.CommandOutputReceived += (target, output) => Ui(() =>
        {
            if (AdminLayer.Visibility == Visibility.Visible) AppendOutput(target, output);
            else AppendSystem($"[{target}] 命令输出：{Shorten(output)}");
        });
    }

    private void EnterChatView(string nick, string sysText)
    {
        _connected = true;
        _adminNick = null;
        LoginCard.Visibility = Visibility.Collapsed;
        ChatPanel.Visibility = Visibility.Visible;
        DisconnectButton.IsEnabled = true;
        SubtitleText.Text = $"已连接 · 昵称 {nick} · {(ModeHost.IsChecked == true ? "主机模式" : "成员模式")}";
        AppendSystem(sysText);
        PassInfoBox.Password = _roomPass;
        InputBox.Focus();
        ResetConnectButton();
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (!_connected)
        {
            StatusText.Text = "未连接";
            return;
        }

        string role = _server != null ? "主机" : "成员";
        string room = string.IsNullOrEmpty(_roomEndpoint) ? "" : $" · 房间 {_roomEndpoint}";
        StatusText.Text = $"{role}模式 · 在线 {_members.Count} 人{room}";
    }

    private void OnDisconnected(string reason)
    {
        if (!_connected) return;
        Disconnect(reason, notify: true);
    }

    private void Disconnect(string reason, bool notify)
    {
        _connected = false;
        Cleanup();

        LoginCard.Visibility = Visibility.Visible;
        ChatPanel.Visibility = Visibility.Collapsed;
        DisconnectButton.IsEnabled = false;
        ResetConnectButton();
        SubtitleText.Text = "LRNC-1024-AEAD 端到端加密 · 同一局域网内主机建房、成员加入";
        AppendSystem($"连接已结束：{reason}");
        UpdateStatus();

        if (notify) Notify($"连接已结束：{reason}", "ClassTalk");
        _plugin.LogInfo($"连接结束：{reason}");
    }

    private void Cleanup()
    {
        try { CloseAdminConsole(); } catch { }
        _adminNick = null;
        try { _announcer?.Dispose(); } catch { }
        _announcer = null;
        try { _client?.Dispose(); } catch { }
        try { _server?.Dispose(); } catch { }
        _client = null;
        _server = null;
        _session = null;
        _hostPort = 0;
        _members = Array.Empty<string>();
        _adminOutput.Clear();
    }

    private void DisconnectButton_Click(object sender, RoutedEventArgs e)
    {
        Disconnect("已主动断开连接", notify: false);
    }

    /// <summary>宿主停用插件时调用：不弹提示，静默断开。</summary>
    public void Shutdown()
    {
        if (_closed) return;
        _closed = true;
        _connected = false;
        try { _scanTimer?.Stop(); } catch { }
        _scanTimer = null;
        try { _scanner?.Dispose(); } catch { }
        _scanner = null;
        Cleanup();
        _plugin.LogInfo("ClassTalk 页面已关闭，网络连接与扫描已释放");
    }

    // ===================== 消息渲染 =====================
    private void SendButton_Click(object sender, RoutedEventArgs e) => SendCurrentMessage();

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            SendCurrentMessage();
            e.Handled = true;
        }
    }

    private async void SendCurrentMessage()
    {
        string text = InputBox.Text.Trim();
        if (text.Length == 0) return;

        // 隐藏指令：/admin —— 竞选管理员（先到先得），不会作为聊天内容发出
        if (text.Equals("/admin", StringComparison.OrdinalIgnoreCase))
        {
            InputBox.Clear();
            await HandleAdminCommandAsync();
            return;
        }

        bool plain = PlainCheck.IsChecked == true;
        try
        {
            if (_client != null)
            {
                await _client.SendAsync(text, plain);
            }
            else if (_server != null)
            {
                if (_server.GetClientEndpoints().Count == 0 && !_emptyRoomHinted)
                {
                    _emptyRoomHinted = true;
                    AppendSystem($"当前没有其他设备连接（房间地址 {_roomEndpoint}），这条消息只有你自己能看到");
                }

                if (plain)
                {
                    // 主机明文通道：本地显示并原样广播 UTF-8 明文帧
                    AppendBubble(_server.HostNick, text, isOwn: true, isPlain: true);
                    _server.BroadcastFromHost(ChatProtocol.FramePlainMessage(_server.HostNick, text));
                }
                else
                {
                    // 主机视角：本地加密、本地显示，并广播密文给所有客户端
                    byte[] enc = _session!.Encrypt(text);
                    AppendBubble(_server.HostNick, text, isOwn: true);
                    _server.BroadcastFromHost(ChatProtocol.FrameMessage(_server.HostNick, enc));
                }
            }
            else
            {
                AppendSystem("尚未连接房间，无法发送消息");
                return;
            }

            InputBox.Clear();
        }
        catch (Exception ex)
        {
            AppendSystem($"发送失败：{ex.Message}");
        }
    }

    private void AppendBubble(string sender, string message, bool isOwn, bool isPlain = false)
    {
        string time = DateTime.Now.ToString("HH:mm");

        var panel = new StackPanel
        {
            Margin = new Thickness(4, 6, 12, 0),
            MaxWidth = 520,
            HorizontalAlignment = isOwn ? HorizontalAlignment.Right : HorizontalAlignment.Left
        };

        var meta = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 0, 2, 4) };

        var avatar = new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(12),
            Background = Res(isOwn ? "AppAccentBrush" : "AppSuccessBrush", isOwn ? Color.FromRgb(0x2F, 0x6B, 0xFF) : Color.FromRgb(0x2E, 0x9E, 0x5B)),
            VerticalAlignment = VerticalAlignment.Center
        };
        avatar.Child = new TextBlock
        {
            Text = SenderInitial(sender),
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        meta.Children.Add(avatar);

        meta.Children.Add(new TextBlock
        {
            Text = sender,
            FontSize = 12,
            Foreground = Res("AppTextSecondaryBrush", Color.FromRgb(0x6B, 0x6B, 0x6B)),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        });

        meta.Children.Add(new TextBlock
        {
            Text = time,
            FontSize = 10,
            Foreground = Res("AppTextSecondaryBrush", Color.FromRgb(0x6B, 0x6B, 0x6B)),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        });

        if (isPlain)
        {
            var tag = new Border
            {
                Background = Res("AppWarningBrush", Color.FromRgb(0xD1, 0x7A, 0x00)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(5, 1, 5, 1),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            tag.Child = new TextBlock
            {
                Text = "明文",
                FontSize = 9,
                Foreground = Brushes.White,
                FontFamily = UiFont
            };
            meta.Children.Add(tag);
        }

        panel.Children.Add(meta);

        var bubble = new Border
        {
            Background = Res(isOwn ? "AppAccentBrush" : "AppLayerAltBrush", isOwn ? Color.FromRgb(0x2F, 0x6B, 0xFF) : Color.FromRgb(0xF1, 0xF2, 0xF5)),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 8, 12, 8),
            BorderBrush = Res("AppStrokeBrush", Color.FromRgb(0xDD, 0xDD, 0xDD)),
            BorderThickness = new Thickness(1)
        };

        var body = new TextBlock
        {
            Text = message,
            Foreground = isOwn ? Brushes.White : Res("AppTextPrimaryBrush", Color.FromRgb(0x1A, 0x1A, 0x1A)),
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = UiFont,
            FlowDirection = FlowDirection.LeftToRight
        };
        // Ideal 排版保证阿拉伯文等复杂脚本正确成形（连写/从右到左），避免字形错乱
        TextOptions.SetTextFormattingMode(body, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(body, TextRenderingMode.ClearType);
        bubble.Child = body;
        panel.Children.Add(bubble);

        MsgPanel.Children.Add(panel);
        MsgScroller.ScrollToEnd();
    }

    private void AppendSystem(string text)
    {
        var tb = new TextBlock
        {
            Text = text,
            Foreground = Res("AppTextSecondaryBrush", Color.FromRgb(0x6B, 0x6B, 0x6B)),
            FontSize = 11,
            Margin = new Thickness(4, 8, 12, 2),
            TextWrapping = TextWrapping.Wrap,
            FontFamily = UiFont,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        MsgPanel.Children.Add(tb);
        MsgScroller.ScrollToEnd();
    }

    private static string SenderInitial(string nick)
    {
        if (string.IsNullOrEmpty(nick)) return "?";
        return char.ToUpperInvariant(nick[0]).ToString();
    }

    private static string Shorten(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        string one = text.Replace("\r", " ").Replace("\n", " ");
        return one.Length <= 120 ? one : one.Substring(0, 120) + "…";
    }

    private void UpdateMembers(IReadOnlyList<string> list)
    {
        _members = list;

        var server = _server;
        string? me = server?.HostNick ?? _client?.Nick;
        string hostNick = server?.HostNick ?? (list.Count > 0 ? list[0] : "");
        var items = list.Select(nick =>
        {
            // 管理权限是隐藏机制，成员列表只展示“房主/我/成员”，不暴露管理员信息
            string role = string.Equals(nick, hostNick, StringComparison.Ordinal) ? "房主"
                : string.Equals(nick, me, StringComparison.Ordinal) ? "我" : "成员";
            return new MemberItem(nick, SenderInitial(nick), role);
        }).ToList();

        MemberList.ItemsSource = items;
        MemberCountText.Text = items.Count.ToString();

        if (AdminLayer.Visibility == Visibility.Visible) RefreshAdminTargets();
        UpdateStatus();
    }

    private sealed record MemberItem(string Nick, string Initial, string RoleText);

    // ===================== 房间信息 =====================
    private void ShowRoomInfo(string text)
    {
        RoomInfoText.Text = text;
        RoomInfoBorder.Visibility = Visibility.Visible;
    }

    private void RoomInfo_Click(object sender, MouseButtonEventArgs e)
    {
        // 主机模式下点一下会重新探测 IP（切换 Wi-Fi / 网线后地址可能变化）
        if (_server != null && _hostPort > 0)
        {
            string now = LocalNetwork.GetLanIpv4();
            if (now != _lanIp && now != "127.0.0.1")
            {
                _lanIp = now;
                HostBox.Text = now;
                LanIpText.Text = now;
                _roomEndpoint = $"{now}:{_hostPort}";
                RoomInfoText.Text = $"房间地址\n{_roomEndpoint}\n（点击复制）";
                AppendSystem($"已更新本机局域网 IP：{now}");
            }
        }

        if (string.IsNullOrWhiteSpace(_roomEndpoint)) return;
        try
        {
            Clipboard.SetText(_roomEndpoint);
            AppendSystem($"已复制房间地址：{_roomEndpoint}");
        }
        catch
        {
            AppendSystem("复制失败，请手动记录地址");
        }
    }

    // ===================== 隐藏的管理员能力 =====================
    /// <summary>
    /// 口令框 / 聊天框输入 /admin 的统一入口：
    /// 本机已是管理员 → 直接打开控制台；尚无管理员 → 申请成为管理员（先到先得）。
    /// </summary>
    private async Task HandleAdminCommandAsync()
    {
        if (IsLocalAdmin())
        {
            OpenAdminConsole();
            return;
        }

        if (_adminNick != null)
        {
            AppendSystem($"管理权限已由 {_adminNick} 持有，无法重复获取");
            return;
        }

        if (_server != null)
        {
            if (_server.TryClaimAdminAsHost()) OpenAdminConsole();
            else AppendSystem("管理权限已被其他人取得");
            return;
        }

        if (_client != null)
        {
            try { await _client.RequestAdminAsync(); }
            catch (Exception ex) { AppendSystem($"获取管理权限失败：{ex.Message}"); }
            return;
        }

        AppendSystem("尚未连接房间，无法使用 /admin");
    }

    /// <summary>登录页口令框回车：等同点“开始连接”。</summary>
    private void PassBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        ConnectButton_Click(ConnectButton, new RoutedEventArgs());
    }

    /// <summary>聊天页口令框回车：等同点“确认口令”。</summary>
    private void PassInfoBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        HandlePassConfirm();
    }

    private void PassConfirm_Click(object sender, RoutedEventArgs e) => HandlePassConfirm();

    /// <summary>
    /// 口令输入处的确认逻辑：
    /// 输入 /admin → 申请或打开管理面板（口令框本身始终回显当前房间口令）；
    /// 输入其他内容 → 记录为新口令，避免中途换口令导致当前房间无法解密，故仅在重连后生效。
    /// </summary>
    private async void HandlePassConfirm()
    {
        string input = PassInfoBox.Password;
        if (input.Length == 0)
        {
            PassInfoBox.Password = _roomPass;
            return;
        }

        if (input.Trim().Equals(AdminCode, StringComparison.OrdinalIgnoreCase))
        {
            PassInfoBox.Password = _roomPass;
            await HandleAdminCommandAsync();
            return;
        }

        if (input == _roomPass)
        {
            Notify("口令未变化", "ClassTalk");
            return;
        }

        _roomPass = input;
        PassInfoBox.Password = _roomPass;
        Notify("口令已记录，重新连接后生效（当前会话仍使用原口令）", "ClassTalk");
    }

    private bool IsLocalAdmin()
    {
        string? me = _server?.HostNick ?? _client?.Nick;
        return me != null && _adminNick == me;
    }

    private void OpenAdminConsole()
    {
        if (!IsLocalAdmin())
        {
            AppendSystem("当前没有管理权限，输入 /admin 可申请");
            return;
        }

        AdminTitleText.Text = $"控制台 · {(_server?.HostNick ?? _client?.Nick)}";
        AdminSubtitleText.Text = "命令经 LRNC 加密通道下发到目标机器执行，输出在下方回显；不支持需要交互输入的命令。";
        AdminLayer.Visibility = Visibility.Visible;
        RefreshAdminTargets();
        CmdBox.Focus();
    }

    private void CloseAdminConsole()
    {
        AdminLayer.Visibility = Visibility.Collapsed;
    }

    private void AdminClose_Click(object sender, RoutedEventArgs e) => CloseAdminConsole();

    /// <summary>可被控的目标：除本机与主机之外的所有在线成员。</summary>
    private IReadOnlyList<string> GetAdminTargets()
    {
        if (_members.Count == 0) return Array.Empty<string>();
        string me = _server?.HostNick ?? _client?.Nick ?? "";
        string host = _server?.HostNick ?? (_members.Count > 0 ? _members[0] : ""); // 客户端视角主机始终是成员列表首位
        return _members.Where(m => m != me && m != host).ToList();
    }

    private void RefreshAdminTargets()
    {
        if (TargetList is not { } listBox) return;
        var targets = GetAdminTargets();
        var selected = listBox.SelectedItems?.Cast<string>().ToList() ?? new List<string>();
        listBox.ItemsSource = targets;
        foreach (var t in targets)
            if (selected.Contains(t) && listBox.SelectedItems is { } items) items.Add(t);
    }

    private void RefreshTargets_Click(object sender, RoutedEventArgs e) => RefreshAdminTargets();

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (TargetList.ItemsSource is not IEnumerable<string> items) return;
        TargetList.SelectedItems.Clear();
        foreach (var i in items.ToList()) TargetList.SelectedItems.Add(i);
    }

    private void Invert_Click(object sender, RoutedEventArgs e)
    {
        if (TargetList.ItemsSource is not IEnumerable<string> items) return;
        var all = items.ToList();
        var selected = TargetList.SelectedItems.Cast<string>().ToHashSet();
        TargetList.SelectedItems.Clear();
        foreach (var i in all.Where(i => !selected.Contains(i))) TargetList.SelectedItems.Add(i);
    }

    private void ClearOutput_Click(object sender, RoutedEventArgs e)
    {
        _adminOutput.Clear();
        OutputBox.Text = "";
    }

    private void QuickCmd_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string cmd })
        {
            CmdBox.Text = cmd;
            CmdBox.CaretIndex = CmdBox.Text.Length;
        }
    }

    private void CmdBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            ExecuteCmd_Click(sender, e);
            e.Handled = true;
        }
    }

    private async void ExecuteCmd_Click(object sender, RoutedEventArgs e)
    {
        string command = CmdBox.Text.Trim();
        if (command.Length == 0)
        {
            AppendOutputLine("系统", "请先输入要执行的命令");
            return;
        }

        var targets = TargetList.SelectedItems?.Cast<string>().ToList() ?? new List<string>();
        if (targets.Count == 0)
        {
            AppendOutputLine("系统", "请先在左侧勾选至少一台目标设备");
            return;
        }

        foreach (var target in targets)
        {
            bool ok = await SendCommandToTargetAsync(target, command);
            if (!ok) AppendOutputLine("系统", $"{target} 不在线或命令发送失败");
            else AppendOutputLine("系统", $"→ {target}：{command}");
        }
    }

    private async Task<bool> SendCommandToTargetAsync(string target, string command)
    {
        try
        {
            if (_server != null) return _server.SendCommandFromHost(target, command);
            if (_client != null)
            {
                await _client.SendCommandAsync(target, command);
                return true;
            }
        }
        catch (Exception ex)
        {
            _plugin.LogError("下发命令失败", ex);
        }

        return false;
    }

    private void OnAdminResult(bool granted, string holder)
    {
        if (granted)
        {
            // 服务端确认授权后本机同步登记管理员身份，避免依赖广播时序导致面板打不开
            _adminNick ??= _client?.Nick ?? _server?.HostNick;
            AppendSystem("已获取管理权限：在口令框或聊天框输入 /admin 可再次打开控制台");
            UpdateMembers(_members);
            OpenAdminConsole();
            return;
        }

        AppendSystem(string.IsNullOrEmpty(holder)
            ? "管理权限已被其他人取得"
            : $"管理权限已由 {holder} 持有");
    }

    /// <summary>管理权变化：只调整本地状态与可见面板，不向房间广播任何提示文案。</summary>
    private void OnAdminChanged(string? nick)
    {
        _adminNick = nick;

        if (!IsLocalAdmin() && AdminLayer.Visibility == Visibility.Visible)
        {
            CloseAdminConsole();
            AppendSystem("管理权限已失效，控制台已关闭");
        }

        UpdateMembers(_members);
    }

    /// <summary>被控端：收到管理员命令后在本机执行 cmd，并把输出回传。</summary>
    private async Task RunRemoteCommandAsync(string adminNick, string command)
    {
        AppendSystem($"收到 {adminNick} 下发的指令，正在本机执行：{command}");
        string output;
        try { output = await CommandRunner.RunAsync(command); }
        catch (Exception ex) { output = "[执行失败] " + ex.Message; }

        AppendSystem($"命令已执行：{command}");

        try
        {
            if (_client != null) await _client.SendCommandOutputAsync(_client.Nick, output);
        }
        catch (Exception ex)
        {
            AppendSystem($"输出回传失败：{ex.Message}");
        }
    }

    private void AppendOutput(string target, string output)
    {
        AppendOutputLine(target, output);
    }

    private void AppendOutputLine(string source, string text)
    {
        _adminOutput.Append('[').Append(DateTime.Now.ToString("HH:mm:ss")).Append("] ")
                     .Append(source).AppendLine("：")
                     .AppendLine(string.IsNullOrWhiteSpace(text) ? "（无输出）" : text)
                     .AppendLine(new string('-', 46));

        // 防止长时间使用把输出缓冲撑爆
        if (_adminOutput.Length > 160_000)
            _adminOutput.Remove(0, _adminOutput.Length - 120_000);

        OutputBox.Text = _adminOutput.ToString();
        OutputBox.ScrollToEnd();
    }

    // ===================== 加密自检 =====================
    private void SelfTestButton_Click(object sender, RoutedEventArgs e)
    {
        TestLayer.Visibility = Visibility.Visible;
    }

    private void TestClose_Click(object sender, RoutedEventArgs e)
    {
        TestLayer.Visibility = Visibility.Collapsed;
    }

    private async void RunTest_Click(object sender, RoutedEventArgs e)
    {
        RunTestButton.IsEnabled = false;
        TestOutputBox.Text = "正在运行自检…";

        string report = await Task.Run(() =>
        {
            var sb = new StringBuilder();
            sb.AppendLine($"LRNC-1024-AEAD 自检 · {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();

            void Section(string name, Func<string> body)
            {
                try
                {
                    string detail = body().Replace("\r", " ").Replace("\n", " ");
                    sb.AppendLine($"[PASS] {name}");
                    sb.AppendLine("       " + detail);
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"[FAIL] {name}");
                    sb.AppendLine("       " + ex.Message);
                }
                sb.AppendLine();
            }

            Section("已知答案向量（KAT）", SelfTest.RunAll);
            Section("Unicode 往返一致性", UnicodeSelfTest.RunAll);
            Section("字体覆盖检查", UnicodeSelfTest.RunFontCoverage);
            Section("TCP 回环端到端收发", LoopbackSelfTest.Run);
            return sb.ToString();
        });

        TestOutputBox.Text = report;
        RunTestButton.IsEnabled = true;
    }
}
