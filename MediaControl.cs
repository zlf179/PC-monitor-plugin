using Windows.Media.Control;

namespace PcMonitor;

/// <summary>
/// 系统媒体会话（SMTC）封装：
///   读取当前会话的歌名/艺术家/进度/播放状态，
///   并提供 播放暂停/上一首/下一首 控制（供 ESP32 按钮远程触发）。
/// 兼容所有接入 SMTC 的播放器（QQ 音乐、网易云、Spotify、浏览器等）。
/// </summary>
public sealed class MediaControl
{
    private GlobalSystemMediaTransportControlsSessionManager? _mgr;
    private GlobalSystemMediaTransportControlsSession? _session;

    public string Title = "";
    public string Artist = "";
    public bool Playing;
    public int PositionSec;   // 当前播放位置（秒）
    public int DurationSec;   // 总时长（秒）
    public bool Active;       // 存在媒体会话

    /// <summary>初始化（异步一次性）。失败返回 false（无媒体服务或权限问题）。</summary>
    public async Task<bool> StartAsync()
    {
        try
        {
            _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (_mgr == null) return false;
            _mgr.SessionsChanged += (s, e) => _session = null;   // 会话增删时强制重绑
            return true;
        }
        catch { return false; }
    }

    /// <summary>刷新当前会话数据（每秒调用）。</summary>
    public void Update()
    {
        Active = false;
        if (_mgr == null) return;
        try
        {
            // 重绑：无绑定或源变更
            _session ??= _mgr.GetCurrentSession();
            if (_session == null) return;
            string sourceId = _session.SourceAppUserModelId;

            var info = _session.GetPlaybackInfo();
            var props = _session.TryGetMediaPropertiesAsync().AsTask().GetAwaiter().GetResult();
            var tl = _session.GetTimelineProperties();

            if (props == null) return;
            Playing = info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            Title = props.Title ?? "";
            Artist = props.Artist ?? "";
            DurationSec = tl.EndTime > TimeSpan.Zero ? (int)tl.EndTime.TotalSeconds : 0;
            // 会话不推进 Position，需用 Position + 实际播放流逝估算
            if (_lastPosCache > TimeSpan.Zero && tl.Position == _lastPosCache && Playing)
                _elapsed = DateTime.UtcNow - _lastPosAt;
            else
                _elapsed = TimeSpan.Zero;
            PositionSec = Math.Max(0, (int)(tl.Position + _elapsed).TotalSeconds);
            _lastPosCache = tl.Position;
            _lastPosAt = DateTime.UtcNow;
            Active = true;
            _sourceId = sourceId;
        }
        catch
        {
            _session = null;   // 会话失效，下轮重绑
        }
    }

    private TimeSpan _lastPosCache = TimeSpan.MinValue;
    private DateTime _lastPosAt = DateTime.UtcNow;
    private TimeSpan _elapsed = TimeSpan.Zero;
    private string _sourceId = "";

    /// <summary>进度百分比（0-100）。无时长返回 0。</summary>
    public int PosPercent =>
        DurationSec > 0 ? Math.Clamp(PositionSec * 100 / DurationSec, 0, 100) : 0;

    /// <summary>响应 ESP32 的控制指令：toggle / prev / next。</summary>
    public async Task ControlAsync(string action)
    {
        try
        {
            _session ??= _mgr?.GetCurrentSession();
            if (_session == null) return;
            switch (action)
            {
                case "toggle":
                    await _session.TryTogglePlayPauseAsync();
                    break;
                case "prev":
                    await _session.TrySkipPreviousAsync();
                    break;
                case "next":
                    await _session.TrySkipNextAsync();
                    break;
            }
            Console.WriteLine($"[media] {action} → {_session.SourceAppUserModelId}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[media] control failed: {ex.Message}");
        }
    }
}
