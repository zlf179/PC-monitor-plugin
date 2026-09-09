using System.Diagnostics;
using System.IO.Ports;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PcMonitor;

/// <summary>
/// 程序→模式 规则（前台进程名或窗口标题包含关键词时，强制进入指定模式）。
/// </summary>
public sealed class AppRule
{
    public string Keyword { get; set; } = "";
    public string Mode { get; set; } = "game";   // game / web / music / idle
}

/// <summary>
/// 配置持久化（%APPDATA%\PcMonitor\config.json）。
/// </summary>
public sealed class Cfg
{
    public string Com { get; set; } = "";
    public string City { get; set; } = "";
    public List<AppRule> Rules { get; set; } = new();
    // 浏览器累计使用时长（电脑开机周期内累计；SysTick+UtcTicks 双校验判断是否同一周期）
    public long WebUseSec { get; set; }
    public long SysTick { get; set; }
    public long UtcTicks { get; set; }
}

/// <summary>
/// 主设置窗口：串口连接 / WiFi 下发 / 程序规则 / 实时状态 / 日志（含 ESP32 日志透传）/ 托盘常驻。
/// 监控循环 1Hz：规则优先 → RTSS 帧率 + LHM 传感器 + SMTC 音乐 → JSON 行 → ESP32。
/// </summary>
public sealed class MainForm : Form
{
    private static readonly string[] Modes = { "game", "web", "music", "idle" };
    private static readonly string[] ModeNames = { "game", "web", "music", "idle" };

    // ---- 采集 / 链路 ----
    private RtssApi _rtss = new();
    private Sensors _sensors = new();
    private readonly MediaControl _media = new();
    private SerialLink? _link;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private bool _monitoring;
    private bool _userStopped;          // 用户手动停止 → 不再自动拉起（等待设备/自动重试除外）
    private bool _reallyExit;
    private int _tickCount;
    private int _autoRetry;             // 设备后插入自动重连计数
    private long _lastOpenFailLog;      // 打开失败限频日志（30s 一条）

    // ---- 浏览器累计使用时长（电脑开机周期内累计，重启电脑清零）----
    private long _webBaseSec;          // 进入当前 web 段之前的累计基数
    private long _webUseSec;           // 当前累计值（推送给设备）
    private long _webEnterTick = -1;   // 进入 web 场景的开机毫秒（-1=不在 web 场景）

    // ---- 规则 ----
    private readonly List<AppRule> _rules = new();

    // ---- 控件 ----
    private ComboBox _portBox = null!;
    private Label _linkLbl = null!;
    private TextBox _ssidBox = null!;
    private TextBox _passBox = null!;
    private Label _wifiLbl = null!;
    private TextBox _cityBox = null!;
    private Label _wxLbl = null!;
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private DataGridView _ruleGrid = null!;
    private Label _sceneLbl = null!, _fpsLbl = null!, _cpuLbl = null!,
                  _gpuLbl = null!, _ramLbl = null!, _musicLbl = null!;
    private TextBox _log = null!;
    private Button _monBtn = null!;
    private CheckBox _autostartCb = null!;
    private NotifyIcon _tray = null!;
    private readonly bool _startHidden;

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "PcMonitor";
    private static string CfgDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PcMonitor");
    private static string CfgFile => Path.Combine(CfgDir, "config.json");

    public MainForm(bool startHidden = false)
    {
        _startHidden = startHidden;
        Text = "PC 桌面伴侣";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5F);
        ClientSize = new Size(458, 792);

        BuildUi();
        _timer.Tick += MonitorTick;
        Load += OnLoad;
        FormClosing += OnFormClosing;
        FormClosed += OnFormClosed;
    }

    private static Label Lbl(int x, int y, int w, string text = "", Color? color = null)
    {
        var l = new Label { Text = text, AutoSize = false, Bounds = new Rectangle(x, y, w, 18) };
        if (color.HasValue) l.ForeColor = color.Value;
        return l;
    }

    // --hidden（开机自启）静默启动：首次可见请求被吞掉，窗口不闪现，直接进托盘
    private bool _shownOnce;
    protected override void SetVisibleCore(bool value)
    {
        if (_startHidden && !_shownOnce)
        {
            _shownOnce = true;
            if (!IsHandleCreated) CreateHandle();
            base.SetVisibleCore(false);
            return;
        }
        base.SetVisibleCore(value);
    }

    protected override void OnShown(EventArgs e)
    {
        _shownOnce = true;
        base.OnShown(e);
    }

    private void BuildUi()
    {
        // 串口
        var gSer = new GroupBox { Text = "串口连接", Bounds = new Rectangle(10, 10, 438, 78) };
        gSer.Controls.Add(Lbl(14, 30, 46, "端口:"));
        _portBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Bounds = new Rectangle(62, 27, 232, 26) };
        gSer.Controls.Add(_portBox);
        var refresh = new Button { Text = "刷新", Bounds = new Rectangle(300, 25, 64, 28) };
        refresh.Click += (s, e) => RefreshPorts();
        gSer.Controls.Add(refresh);
        _linkLbl = Lbl(62, 56, 360, "未连接", Color.DimGray);
        gSer.Controls.Add(_linkLbl);
        Controls.Add(gSer);

        // WiFi 配置（下发到 ESP32，保存在设备 NVS，无需重烧固件）
        var gWifi = new GroupBox { Text = "WiFi 配置（下发后保存在 ESP32）", Bounds = new Rectangle(10, 94, 438, 116) };
        gWifi.Controls.Add(Lbl(14, 32, 48, "SSID:"));
        _ssidBox = new TextBox { Bounds = new Rectangle(66, 28, 226, 24) };
        gWifi.Controls.Add(_ssidBox);
        gWifi.Controls.Add(Lbl(14, 64, 48, "密码:"));
        _passBox = new TextBox { Bounds = new Rectangle(66, 60, 226, 24), UseSystemPasswordChar = true };
        gWifi.Controls.Add(_passBox);
        var send = new Button { Text = "下发", Bounds = new Rectangle(300, 26, 64, 26) };
        send.Click += OnSendWifi;
        gWifi.Controls.Add(send);
        var clear = new Button { Text = "清除", Bounds = new Rectangle(300, 58, 64, 26) };
        clear.Click += OnClearWifi;
        gWifi.Controls.Add(clear);
        _wifiLbl = Lbl(66, 88, 360, "未查询", Color.DimGray);
        gWifi.Controls.Add(_wifiLbl);
        Controls.Add(gWifi);

        // 天气地区（输入城市名，自动查经纬度后下发给 ESP32）
        var gWx = new GroupBox { Text = "天气地区（输入城市名，自动解析经纬度下发）", Bounds = new Rectangle(10, 218, 438, 78) };
        gWx.Controls.Add(Lbl(14, 32, 62, "城市/地区:"));
        _cityBox = new TextBox { Bounds = new Rectangle(80, 28, 180, 24) };
        gWx.Controls.Add(_cityBox);
        var wxSend = new Button { Text = "下发", Bounds = new Rectangle(280, 26, 64, 26) };
        wxSend.Click += OnSendWx;
        gWx.Controls.Add(wxSend);
        _wxLbl = Lbl(14, 58, 412, "未查询", Color.DimGray);
        gWx.Controls.Add(_wxLbl);
        Controls.Add(gWx);

        // 程序→模式 规则
        var gRules = new GroupBox { Text = "程序模式规则（前台进程名/标题包含关键词 → 强制进入该模式）", Bounds = new Rectangle(10, 302, 438, 150) };
        _ruleGrid = new DataGridView
        {
            Bounds = new Rectangle(14, 24, 312, 116),
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            EditMode = DataGridViewEditMode.EditOnEnter,
        };
        var colKw = new DataGridViewTextBoxColumn
        {
            HeaderText = "关键词（进程名/标题）",
            Name = "Keyword",
            FillWeight = 68,
        };
        var colMode = new DataGridViewComboBoxColumn
        {
            HeaderText = "模式",
            Name = "Mode",
            DataSource = Modes.ToList(),
            FlatStyle = FlatStyle.Flat,
            FillWeight = 32,
        };
        _ruleGrid.Columns.AddRange(colKw, colMode);
        _ruleGrid.CellValueChanged += (s, e) => SaveRules();
        _ruleGrid.UserDeletedRow += (s, e) => SaveRules();
        _ruleGrid.DataError += (s, e) => { e.ThrowException = false; };   // 编辑中途的无效值忽略
        gRules.Controls.Add(_ruleGrid);
        var addBtn = new Button { Text = "添加规则", Bounds = new Rectangle(334, 24, 92, 30) };
        addBtn.Click += (s, e) =>
        {
            _ruleGrid.Rows.Add("", "game");
            SaveRules();
        };
        gRules.Controls.Add(addBtn);
        var delBtn = new Button { Text = "删除选中", Bounds = new Rectangle(334, 58, 92, 30) };
        delBtn.Click += (s, e) =>
        {
            if (_ruleGrid.CurrentRow != null)
            {
                _ruleGrid.Rows.Remove(_ruleGrid.CurrentRow);
                SaveRules();
            }
        };
        gRules.Controls.Add(delBtn);
        var hint = Lbl(334, 96, 100, "例: Steam→game\n例: cloudmusic→music");
        gRules.Controls.Add(hint);
        Controls.Add(gRules);

        // 运行状态
        var gSt = new GroupBox { Text = "运行状态", Bounds = new Rectangle(10, 460, 438, 178) };
        _sceneLbl = Lbl(14, 26, 410, "场景: -");
        _fpsLbl = Lbl(14, 50, 410, "FPS: -");
        _cpuLbl = Lbl(14, 74, 410, "CPU: -");
        _gpuLbl = Lbl(14, 98, 410, "GPU: -");
        _ramLbl = Lbl(14, 122, 410, "内存: -");
        _musicLbl = Lbl(14, 146, 410, "音乐: -");
        gSt.Controls.AddRange(new Control[] { _sceneLbl, _fpsLbl, _cpuLbl, _gpuLbl, _ramLbl, _musicLbl });
        Controls.Add(gSt);

        // 日志（含 ESP32 端日志透传，可 debug WiFi 连接失败原因）
        _log = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            Bounds = new Rectangle(10, 644, 438, 108),
            Font = new Font("Consolas", 8.5F),
        };
        Controls.Add(_log);

        // 底部
        _monBtn = new Button { Text = "停止监控", Bounds = new Rectangle(10, 758, 96, 30) };
        _monBtn.Click += (s, e) => { if (_monitoring) StopMonitoring(); else StartMonitoring(); };
        Controls.Add(_monBtn);

        _autostartCb = new CheckBox { Text = "开机自启", AutoSize = true, Bounds = new Rectangle(116, 763, 100, 24) };
        _autostartCb.CheckedChanged += (s, e) => SetAutostart(_autostartCb.Checked);
        Controls.Add(_autostartCb);
    }

    private async void OnLoad(object? sender, EventArgs e)
    {
        RefreshPorts();
        var cfg = LoadConfig();
        if (cfg.City.Length > 0) _cityBox.Text = cfg.City;
        // 恢复浏览器累计时长：开机 tick 与墙上时间都前进 = 同一电脑周期（客户端重启不清零，电脑重启清零）
        if (cfg.SysTick > 0 && cfg.UtcTicks > 0 &&
            cfg.SysTick < Environment.TickCount64 &&
            cfg.UtcTicks <= DateTime.UtcNow.Ticks)
        {
            _webBaseSec = cfg.WebUseSec;
            _webUseSec = cfg.WebUseSec;
        }
        LoadRules();
        _autostartCb.Checked = GetAutostart();
        SetupTray();
        if (_portBox.SelectedIndex >= 0) StartMonitoring();   // 有串口则自动开始
        _timer.Start();   // 即使未连上也跑 tick：设备晚于客户端枚举时自动重连
        if (!await _media.StartAsync())
            Log("媒体会话不可用（音乐页/音乐控制不可用）");
    }

    // ---- 监控启停 ----

    private void StartMonitoring()
    {
        if (_monitoring) return;
        string? port = _portBox.SelectedItem as string;
        if (string.IsNullOrEmpty(port)) return;   // 无端口：静默等 tick 重试（设备可能未插入）

        var link = new SerialLink(port);
        link.LineReceived += OnEspLine;
        link.Open();
        if (!link.Connected)
        {
            link.Dispose();
            if (Environment.TickCount64 - _lastOpenFailLog > 30000)   // 限频，自动重试时不刷屏
            {
                _lastOpenFailLog = Environment.TickCount64;
                Log($"打开 {port} 失败（被其他程序占用？稍后自动重试）");
                _linkLbl.Text = "等待设备…";
            }
            return;
        }

        _link = link;
        _monitoring = true;
        _userStopped = false;
        _monBtn.Text = "停止监控";
        _linkLbl.Text = $"已连接 {port}";
        _linkLbl.ForeColor = Color.ForestGreen;

        _sensors = new Sensors();
        try { _sensors.Start(); }
        catch (Exception ex) { Log($"传感器初始化异常: {ex.Message}"); }

        _link.Send("{\"t\":\"getwifi\"}");   // 顺带查询设备当前 WiFi 状态
        _timer.Start();
        Log($"监控已启动（{port}）");
    }

    private void StopMonitoring()
    {
        if (!_monitoring) return;
        _monitoring = false;
        _userStopped = true;   // 手动停止：tick 不再自动拉起
        _timer.Stop();
        _monBtn.Text = "开始监控";
        _linkLbl.Text = "未连接";
        _linkLbl.ForeColor = Color.DimGray;
        Log("监控已停止");
        // 串口/传感器清理放后台：SerialPort.Close 与 LHM 关闭耗时数百 ms~秒级，不阻塞 UI
        var link = _link;
        var sensors = _sensors;
        _link = null;
        _sensors = new Sensors();
        Task.Run(() =>
        {
            try { link?.Dispose(); } catch { }
            try { sensors.Dispose(); } catch { }
        });
    }

    // ---- 1Hz 监控循环：规则优先 → 自动判定 ----

    private void MonitorTick(object? sender, EventArgs e)
    {
        // 未监控且非用户手动停止：每 5s 自动重试（设备晚于客户端枚举/开机自启场景）
        if (!_monitoring && !_userStopped && ++_autoRetry >= 5)
        {
            _autoRetry = 0;
            if (_portBox.SelectedIndex < 0) RefreshPorts(quiet: true);   // 重新探测（VID 识别）
            StartMonitoring();
        }
        if (!_monitoring || _link == null) return;
        try
        {
            if (!_link.Connected)
            {
                _linkLbl.Text = "重连中…";
                _link.Open();
                if (_link.Connected) _linkLbl.Text = "已连接";
            }

            ForegroundApp.Update();
            _rtss.Update();
            try { _sensors.Update(); } catch { }
            _media.Update();

            // WiFi 状态轮询（10s 一次，更新 _wifiLbl）
            if (++_tickCount % 10 == 0)
                _link.Send("{\"t\":\"getwifi\"}");

            // 浏览器累计时长持久化（每分钟一次，防客户端异常退出丢进度）
            if (_tickCount % 60 == 0)
                SaveConfig();

            // 规则匹配（优先于自动判定）
            string? rule = MatchRule();
            // 游戏判定：产生帧的进程必须是前台进程（游戏切后台后 RTSS 仍统计其后台渲染帧，
            // 只看 FPS 阈值会导致 game/idle 每秒跳变）
            string rtssProc = Path.GetFileNameWithoutExtension(_rtss.AppName);
            bool gameForeground = rtssProc.Length > 0 &&
                rtssProc.Equals(ForegroundApp.ProcessName, StringComparison.OrdinalIgnoreCase) &&
                _rtss.Fps > 0;
            bool isGame = gameForeground;
            if (!isGame && ForegroundApp.IsFullscreenGame) isGame = true;

            string scene, json;
            bool isWebScene = false;
            if (rule == "game" || (rule == null && isGame))
            {
                scene = rule == "game" ? "游戏(规则)" : "游戏";
                json = GameJson();
            }
            else if (rule == "web" || (rule == null && ForegroundApp.IsBrowser))
            {
                scene = rule == "web" ? "浏览器(规则)" : "浏览器";
                json = WebJson();
                isWebScene = true;
            }
            else if (rule == "music" || (rule == null && _media.Active && (_media.Playing || _media.Title.Length > 0)))
            {
                // 有媒体会话（含暂停中）就推送：暂停时也发 pl=0，
                // 否则 ESP32 音乐页的播放键图标/时间会卡在旧状态
                scene = rule == "music" ? "音乐(规则)" : "音乐";
                json = MusicJson();
            }
            else
            {
                scene = rule == "idle" ? "待机(规则)" : "待机";
                json = IdleJson();
            }

            // 浏览器累计使用时长：web 场景（前台浏览器/规则指定）计时，离开定格累计
            long nowTick = Environment.TickCount64;
            if (isWebScene)
            {
                if (_webEnterTick < 0) _webEnterTick = nowTick;
                _webUseSec = _webBaseSec + (nowTick - _webEnterTick) / 1000;
            }
            else if (_webEnterTick >= 0)
            {
                _webBaseSec = _webUseSec;
                _webEnterTick = -1;
            }

            _link.Send(json);
            UpdateStatusLabels(scene, scene.StartsWith("游戏"));
        }
        catch (Exception ex)
        {
            Log($"tick 异常: {ex.Message}");
        }
    }

    /// <summary>规则匹配：前台进程名或窗口标题包含关键词（不分大小写）→ 返回模式，否则 null。</summary>
    private string? MatchRule()
    {
        string proc = ForegroundApp.ProcessName ?? "";
        string title = ForegroundApp.WindowTitle ?? "";
        foreach (var r in _rules)
        {
            if (r.Keyword.Length == 0) continue;
            if (proc.Contains(r.Keyword, StringComparison.OrdinalIgnoreCase) ||
                title.Contains(r.Keyword, StringComparison.OrdinalIgnoreCase))
                return r.Mode;
        }
        return null;
    }

    // ---- 各场景 JSON（都带 clock，固件全场景解析用于待机页时钟）----

    private static string NowClock => DateTime.Now.ToString("HH:mm:ss");

    private string GameJson() => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["t"] = "g",
        ["fps"] = Math.Round(_rtss.Fps > 0 ? _rtss.Fps : 0, 0),
        ["ft"] = _rtss.Fps > 0 ? Math.Round(1000f / _rtss.Fps, 1) : 0,
        ["cpu"] = Math.Round(_sensors.CpuLoad, 0),
        ["cpuT"] = Math.Round(_sensors.CpuTemp, 0),
        ["cpuV"] = Math.Round(_sensors.CpuVoltage, 2),
        ["cpuF"] = Math.Round(_sensors.CpuClock, 0),
        ["cpuW"] = Math.Round(_sensors.CpuPower, 0),
        ["gpu"] = Math.Round(_sensors.GpuLoad, 0),
        ["gpuT"] = Math.Round(_sensors.GpuTemp, 0),
        ["gpuF"] = Math.Round(_sensors.GpuCoreClock, 0),
        ["gpuM"] = Math.Round(_sensors.GpuMemUsed, 0),
        ["gpuW"] = Math.Round(_sensors.GpuPower, 0),
        ["ram"] = Math.Round(_sensors.RamUsed, 0),
        ["clock"] = NowClock,
    });

    private string WebJson()
    {
        var boot = TimeSpan.FromMilliseconds(Environment.TickCount64);
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["t"] = "w",
            ["clock"] = NowClock,
            ["use"] = _webUseSec,   // 浏览器累计使用秒（电脑开机周期内累计，WEB 页大字显示）
            ["up"] = $"{(int)boot.TotalHours}:{boot.Minutes:D2}:{boot.Seconds:D2}",
            ["title"] = Trunc(ForegroundApp.WindowTitle, 44),
        });
    }

    private string MusicJson()
    {
        // 规则强制音乐模式但 SMTC 无数据时，给占位提示（进度环/按钮仍可用）
        bool has = _media.Active;
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["t"] = "m",
            ["title"] = has ? Trunc(_media.Title, 46) : "PC 播放器未接入 SMTC",
            ["artist"] = has ? Trunc(_media.Artist, 46) : "在播放器里播放任意歌曲试试",
            ["pos"] = has ? _media.PosPercent : 0,
            ["cur"] = has ? _media.PositionSec : 0,
            ["dur"] = has ? _media.DurationSec : 0,
            ["pl"] = has && _media.Playing ? 1 : 0,
            ["clock"] = NowClock,
        });
    }

    private static string IdleJson() => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["t"] = "i",
        ["clock"] = NowClock,
    });

    private void UpdateStatusLabels(string scene, bool isGame)
    {
        _sceneLbl.Text = $"场景: {scene}";
        if (isGame)
        {
            _fpsLbl.Text = $"FPS {_rtss.Fps:0}  帧时 {(_rtss.Fps > 0 ? 1000f / _rtss.Fps : 0):0.0} ms";
            _cpuLbl.Text = $"CPU {_sensors.CpuLoad:0}%  {_sensors.CpuTemp:0}°C  {_sensors.CpuPower:0}W  {_sensors.CpuClock / 1000f:0.00}GHz";
            _gpuLbl.Text = $"GPU {_sensors.GpuLoad:0}%  {_sensors.GpuTemp:0}°C  {_sensors.GpuPower:0}W  {_sensors.GpuCoreClock / 1000f:0.00}GHz";
            _ramLbl.Text = $"内存 {_sensors.RamUsed:0}%";
        }
        else
        {
            _fpsLbl.Text = "FPS -";
            _cpuLbl.Text = "CPU -";
            _gpuLbl.Text = "GPU -";
            _ramLbl.Text = "内存 -";
        }
        _musicLbl.Text = _media.Active && _media.Title.Length > 0
            ? $"音乐: {Trunc(_media.Title, 32)} - {Trunc(_media.Artist, 18)}"
            : "音乐: -";
    }

    // ---- WiFi 下发 ----

    private void OnSendWifi(object? sender, EventArgs e)
    {
        string ssid = _ssidBox.Text.Trim();
        if (ssid.Length == 0) { Log("请输入 SSID"); return; }
        if (!_monitoring) StartMonitoring();
        if (_link?.Connected != true) { Log("串口未连接，无法下发"); return; }

        _link.Send(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["t"] = "wifi",
            ["ssid"] = ssid,
            ["pass"] = _passBox.Text,
        }));
        _wifiLbl.Text = $"已下发 {ssid}，等待设备确认…";
        _wifiLbl.ForeColor = Color.DimGray;
        Log($"WiFi 凭据已下发: {ssid}");
    }

    private void OnClearWifi(object? sender, EventArgs e)
    {
        if (!_monitoring) StartMonitoring();
        if (_link?.Connected != true) { Log("串口未连接"); return; }

        _link.Send("{\"t\":\"wifi\",\"ssid\":\"\",\"pass\":\"\"}");
        _wifiLbl.Text = "已发送清除指令…";
        Log("已下发 WiFi 清除指令（设备回退固件内置配置）");
    }

    // ---- 天气地区下发（城市名 → Open-Meteo 地理编码 → 经纬度下发）----

    private async void OnSendWx(object? sender, EventArgs e)
    {
        string city = _cityBox.Text.Trim();
        if (city.Length == 0) { Log("请输入城市/地区名（例: 北京 / 成都 / Shanghai）"); return; }
        if (!_monitoring) StartMonitoring();
        if (_link?.Connected != true) { Log("串口未连接，无法下发"); return; }

        _wxLbl.Text = $"正在查询「{city}」…";
        _wxLbl.ForeColor = Color.DimGray;

        double lat, lon;
        string dispName;
        try
        {
            (lat, lon, dispName) = await GeocodeAsync(city);
        }
        catch (Exception ex)
        {
            _wxLbl.Text = $"查询失败: {ex.Message}";
            _wxLbl.ForeColor = Color.Firebrick;
            Log($"地理编码失败: {ex.Message}");
            return;
        }

        string latS = lat.ToString("0.##"), lonS = lon.ToString("0.##");
        _link.Send(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["t"] = "wx",
            ["lat"] = latS,
            ["lon"] = lonS,
        }));
        _wxLbl.Text = $"已下发 {dispName} ({latS},{lonS})，等待设备重取天气…";
        Log($"天气地区: {city} → {dispName} ({latS},{lonS})");
    }

    /// <summary>Open-Meteo 地理编码：城市名 → (纬度, 经度, 显示名)。免 key。</summary>
    private static async Task<(double Lat, double Lon, string Name)> GeocodeAsync(string name)
    {
        string url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(name)}&count=1&language=zh&format=json";
        using var resp = await _http.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        if (!doc.RootElement.TryGetProperty("results", out var results) ||
            results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0)
            throw new Exception($"找不到「{name}」，试试更正式的地名");
        var first = results[0];
        double lat = first.GetProperty("latitude").GetDouble();
        double lon = first.GetProperty("longitude").GetDouble();
        string disp = first.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } ? n.GetString()! : name;
        return (lat, lon, disp);
    }

    // ---- ESP32 → PC 行消息（串口接收线程 → UI 线程）----
    // JSON 协议消息走结构化处理；ESP32 端 IDF 日志（非 JSON 行）透传显示，便于 debug。

    private void OnEspLine(string line)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(() => HandleEspLine(line)); }
        catch { /* 窗体已关闭 */ }
    }

    private void HandleEspLine(string line)
    {
        if (line.Length == 0) return;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            string? t = r.TryGetProperty("t", out var tv) ? tv.GetString() : null;

            if (t == "wifi")
            {
                if (r.TryGetProperty("ok", out var ok))
                {
                    bool okv = ok.GetBoolean();
                    _wifiLbl.Text = okv ? "设备已保存并开始连接" : "设备保存失败（NVS 错误）";
                    _wifiLbl.ForeColor = okv ? Color.ForestGreen : Color.Firebrick;
                    Log(_wifiLbl.Text);
                }
                else if (r.TryGetProperty("ssid", out var ssid))
                {
                    string s = ssid.GetString() ?? "";
                    bool up = r.TryGetProperty("up", out var u) && u.GetBoolean();
                    _wifiLbl.Text = s.Length > 0
                        ? $"设备当前: {s}（{(up ? "已联网" : "未连接")}）"
                        : "设备未配置 WiFi";
                    _wifiLbl.ForeColor = up ? Color.ForestGreen : Color.DimGray;

                    // 天气地区 + 设备时钟（getwifi 扩展字段，10s 轮询刷新）
                    if (r.TryGetProperty("lat", out var lat) && r.TryGetProperty("lon", out var lon))
                    {
                        string la = lat.GetString() ?? "", lo = lon.GetString() ?? "";
                        string time = r.TryGetProperty("time", out var tw) ? tw.GetString() ?? "" : "";
                        if (la.Length > 0 && time != "--:--:--")
                            _wxLbl.Text = $"设备当前: {la},{lo} · 设备时钟 {time}";
                        else if (la.Length > 0)
                            _wxLbl.Text = $"设备当前: {la},{lo}（时钟未同步）";
                    }
                }
            }
            else if (t == "wx")
            {
                if (r.TryGetProperty("ok", out var ok))
                {
                    bool okv = ok.GetBoolean();
                    _wxLbl.Text = okv ? "设备已保存，正在重取天气" : "设备保存失败（NVS 错误）";
                    _wxLbl.ForeColor = okv ? Color.ForestGreen : Color.Firebrick;
                    Log(_wxLbl.Text);
                }
            }
            else if (t == "ctrl" && r.TryGetProperty("a", out var a))
            {
                _ = _media.ControlAsync(a.GetString() ?? "");   // ESP32 音乐按钮
            }
        }
        catch
        {
            // 非 JSON 行 = ESP32 端日志（如 wifi connecting/got ip/disconnected reason=15 密码错）
            Log($"[esp] {Trunc(line, 180)}");
        }
    }

    // ---- 串口列表 ----

    private void RefreshPorts(bool quiet = false)
    {
        string auto = AutoDetectPort();
        _portBox.Items.Clear();
        foreach (var p in SerialPort.GetPortNames().Distinct().OrderBy(NumericComPort))
            _portBox.Items.Add(p);

        var cfg = LoadConfig();
        int sel = auto.Length > 0 ? _portBox.Items.IndexOf(auto) : -1;
        if (sel < 0 && cfg.Com.Length > 0) sel = _portBox.Items.IndexOf(cfg.Com);
        if (sel < 0 && _portBox.Items.Count > 0) sel = 0;
        if (sel >= 0) _portBox.SelectedIndex = sel;
        if (!quiet)
            Log(auto.Length > 0 ? $"自动探测到 ESP32: {auto}" : "未探测到 ESP32（USB-Serial-JTAG）");
    }

    // COMx 按数字排序（COM2 < COM10；字符串排序会错）
    private static int NumericComPort(string p) =>
        int.TryParse(p.AsSpan(3), out int n) ? n : int.MaxValue;

    /// <summary>
    /// 自动探测 ESP32 的 USB-Serial-JTAG 端口。
    /// 按 DeviceID VID_303A&amp;PID_1001 匹配（Espressif 厂商 ID）；
    /// 显示名不可靠：中文系统下是"USB 串行设备 (COMx)"，不含 JTAG/303A 字样。
    /// </summary>
    private static string AutoDetectPort()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT Name, DeviceID FROM Win32_PnPEntity WHERE DeviceID LIKE '%VID_303A&PID_1001%'");
            foreach (var mo in searcher.Get())
            {
                string name = mo["Name"]?.ToString() ?? "";
                var m = Regex.Match(name, @"\((COM\d+)\)");
                if (m.Success) return m.Groups[1].Value;
            }
        }
        catch { }
        return "";
    }

    // ---- 规则持久化（与端口共用 config.json）----

    private void LoadRules()
    {
        _rules.Clear();
        foreach (var r in LoadConfig().Rules)
            if (r.Keyword.Length > 0 && Modes.Contains(r.Mode))
                _rules.Add(r);
        foreach (var r in _rules)
            _ruleGrid.Rows.Add(r.Keyword, r.Mode);
    }

    private void SaveRules()
    {
        _rules.Clear();
        foreach (DataGridViewRow row in _ruleGrid.Rows)
        {
            string kw = row.Cells["Keyword"].Value?.ToString()?.Trim() ?? "";
            string mode = row.Cells["Mode"].Value?.ToString() ?? "game";
            if (kw.Length > 0 && Modes.Contains(mode))
                _rules.Add(new AppRule { Keyword = kw, Mode = mode });
        }
        SaveConfig();
    }

    private void SaveConfig()
    {
        try
        {
            Directory.CreateDirectory(CfgDir);
            var cfg = new Cfg
            {
                Com = _portBox.SelectedItem as string ?? "",
                City = _cityBox.Text.Trim(),
                Rules = _rules.ToList(),
                WebUseSec = _webUseSec,
                SysTick = Environment.TickCount64,
                UtcTicks = DateTime.UtcNow.Ticks,
            };
            File.WriteAllText(CfgFile, JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private static Cfg LoadConfig()
    {
        try
        {
            if (File.Exists(CfgFile))
                return JsonSerializer.Deserialize<Cfg>(File.ReadAllText(CfgFile)) ?? new Cfg();
        }
        catch { }
        return new Cfg();
    }

    // ---- 托盘 ----

    private void SetupTray()
    {
        _tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "PC 桌面伴侣", Visible = true };
        var menu = new ContextMenuStrip();
        menu.Items.Add("显示主窗口", null, (s, e) => ShowMain());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (s, e) => { _reallyExit = true; Close(); });
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (s, e) => ShowMain();
    }

    private void ShowMain()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    // ---- 开机自启（计划任务）----
    // 注意：本程序 manifest 要求管理员权限，HKCU Run 键的自启项在登录时会被系统
    // 静默跳过（提权 exe 不允许从 Run 启动）。改用 schtasks 计划任务：
    // 登录时以最高权限启动，无 UAC 弹窗。程序本身已提权，可直接创建。

    private static bool GetAutostart()
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks", $"/query /tn {AppName}")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(3000);
            return p?.ExitCode == 0;
        }
        catch { return false; }
    }

    private static void SetAutostart(bool on)
    {
        try
        {
            if (on && Environment.ProcessPath is { Length: > 0 } exe)
            {
                var psi = new ProcessStartInfo("schtasks",
                    $"/create /tn {AppName} /tr \"\\\"{exe}\\\" /hidden\" /sc onlogon /rl highest /f")
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                };
                using var p = Process.Start(psi);
                p?.WaitForExit(5000);
            }
            else
            {
                var psi = new ProcessStartInfo("schtasks", $"/delete /tn {AppName} /f")
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                };
                using var p = Process.Start(psi);
                p?.WaitForExit(3000);
            }
            // 清理历史版本写过的 Run 键残留（无效的自启方式）
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                k.DeleteValue(AppName, false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[autostart] schtasks 失败: {ex.Message}");
        }
    }

    // ---- 杂项 ----

    private void Log(string msg)
    {
        if (_log.IsDisposed) return;
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}\r\n");
        if (_log.TextLength > 24000) _log.Text = _log.Text[^12000..];
    }

    private static string Trunc(string s, int n) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..(n - 1)] + "…");

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_reallyExit && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            _tray.ShowBalloonTip(2000, "PC 桌面伴侣", "已最小化到托盘，双击图标恢复", ToolTipIcon.Info);
            SaveConfig();
        }
    }

    private void OnFormClosed(object? sender, FormClosedEventArgs e)
    {
        _timer.Stop();
        _link?.Dispose();
        try { _sensors.Dispose(); } catch { }
        _rtss.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        SaveConfig();
    }
}
