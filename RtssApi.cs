using System.Runtime.InteropServices;
using System.Text;

namespace PcMonitor;

/// <summary>
/// RTSS（RivaTuner Statistics Server）共享内存接口，差分计算 FPS。
/// 兼容两种布局：
///  - 旧版 SDK（"RTSSShar" 签名）：头 32B + 512B entry，entry+0x2C=time / +0x30=frames
///  - 新版 RTSS 7.x（"SSTR" 签名）：头 0x08=entrySize(0x3080)，0x0C=entry 区偏移(0x249060)；
///    entry 内 +0x390=µs 时钟、+0x398=帧计数（实测 WorldOfTanks entry 验证）
/// RTSS 只 hook 3D 应用，帧计数在涨即为活跃游戏。
/// </summary>
public sealed class RtssApi : IDisposable
{
    private const string MapName = "RTSSSharedMemoryV2";
    private const int MaxEntries = 64;

    private IntPtr _hMap = IntPtr.Zero;
    private IntPtr _pView = IntPtr.Zero;
    private int _viewSize = 0;
    private bool _ok = false;

    // 每个 entry 的快照（用于差分）
    private readonly uint[] _lastFrames = new uint[MaxEntries];
    private readonly uint[] _lastTime = new uint[MaxEntries];
    private readonly bool[] _seen = new bool[MaxEntries];
    private readonly float[] _fps = new float[MaxEntries];
    private readonly string[] _app = new string[MaxEntries];

    public float Fps => _fps[0];
    public string AppName => _app[0] ?? "";
    public bool Available => _ok;

    public RtssApi()
    {
        _hMap = OpenFileMapping(0x0004 /* FILE_MAP_READ */, false, MapName);
        if (_hMap == IntPtr.Zero) return;
        _pView = MapViewOfFile(_hMap, 0x0004, 0, 0, 0);
        if (_pView == IntPtr.Zero) return;
        _viewSize = QueryMappedSize(_pView);
        _ok = _viewSize >= 64;
    }

    /// <summary>读取共享内存并差分计算各 entry 的 FPS。</summary>
    public void Update()
    {
        if (!_ok) return;
        unsafe
        {
            byte* p = (byte*)_pView.ToPointer();
            string sig = Encoding.ASCII.GetString(p, 4);
            if (sig == "SSTR")
                UpdateNewLayout(p);            // RTSS 7.x 新布局
            else if (sig.StartsWith("RTSS"))
                UpdateOldLayout(p);            // 旧 SDK 布局
            else
                _ok = false;
        }
    }

    // ---- 新版 RTSS 7.x（"SSTR"）----
    private unsafe void UpdateNewLayout(byte* p)
    {
        uint entrySize = *(uint*)(p + 0x08);
        uint entryOff = *(uint*)(p + 0x0C);
        if (entrySize < 256 || entrySize > 65536 || (entrySize & 3) != 0 ||
            entryOff < 32 || entryOff >= (uint)_viewSize)
        { _ok = false; return; }

        int n = (int)Math.Min((_viewSize - entryOff) / entrySize, MaxEntries);

        for (int i = 0; i < n; i++)
        {
            byte* e = p + entryOff + i * entrySize;
            string name = ReadAnsi(e + 4, 256);
            uint time = *(uint*)(e + 0x390);     // µs 时钟
            uint frames = *(uint*)(e + 0x398);   // 帧计数

            if (!_seen[i])
            {
                _seen[i] = true;                 // 首拍只记录基线
            }
            else if (frames != _lastFrames[i] && time != _lastTime[i])
            {
                // uint 无符号减法：时钟回绕（71 分钟）时差值依然数学正确
                uint dFrames = frames - _lastFrames[i];
                uint dTime = time - _lastTime[i];
                float fps = dFrames * 1_000_000f / dTime;
                _fps[i] = dTime > 0 && fps < 2000 ? fps : 0;
            }
            else
            {
                _fps[i] = 0;                     // 帧计数没涨 → 非活跃
            }
            _lastFrames[i] = frames;
            _lastTime[i] = time;
            _app[i] = name;
        }
        for (int i = n; i < MaxEntries; i++) { _fps[i] = 0; _app[i] = ""; }

        PickBest();
    }

    // ---- 旧版 SDK（"RTSSSharedMemoryV2" 老布局）----
    private unsafe void UpdateOldLayout(byte* p)
    {
        for (int i = 1; i < 9; i++)
        {
            byte* e = p + 32 + i * 512;
            string entrySig = Encoding.ASCII.GetString(e, 5);
            if (entrySig != "Entry") { _fps[i - 1] = 0; _app[i - 1] = ""; continue; }

            string name = ReadAnsi(e + 8, 32);
            uint time = *(uint*)(e + 0x2C);
            uint frames = *(uint*)(e + 0x30);

            if (frames < _lastFrames[i] || name != _app[i])
            {
                _lastFrames[i] = frames;
                _lastTime[i] = time;
            }
            else if (time > _lastTime[i])
            {
                float fps = (frames - _lastFrames[i]) * 1000f / (time - _lastTime[i]);
                _fps[i - 1] = fps > 0 && fps < 2000 ? fps : 0;
                _lastFrames[i] = frames;
                _lastTime[i] = time;
            }
            _app[i] = name;
        }
        PickBest();
    }

    private void PickBest()
    {
        int best = -1;
        for (int i = 0; i < MaxEntries; i++)
            if (_fps[i] > 0 && (best < 0 || _fps[i] > _fps[best])) best = i;
        _fps[0] = best >= 0 ? _fps[best] : 0;
        _app[0] = best >= 0 ? (_app[best] ?? "") : "";
    }

    private static unsafe string ReadAnsi(byte* s, int max)
    {
        int len = 0;
        while (len < max && s[len] != 0) len++;
        return len == 0 ? "" : Encoding.ASCII.GetString(s, len);
    }

    /// <summary>累计同一 allocation（AllocationBase 相同）的 MEM_MAPPED 区大小，防止越界读。</summary>
    private static int QueryMappedSize(IntPtr baseAddr)
    {
        int total = 0;
        IntPtr addr = baseAddr;
        for (int guard = 0; guard < 1024; guard++)
        {
            var mbi = new MEMORY_BASIC_INFORMATION();
            if (VirtualQuery(addr, ref mbi, (uint)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()) == 0) break;
            if (mbi.Type != 0x40000 /*MEM_MAPPED*/ || mbi.AllocationBase != baseAddr) break;
            long sz = mbi.RegionSize.ToInt64();
            if (sz <= 0) break;
            total += (int)sz;
            addr += (nint)sz;
            if (total > 64 * 1024 * 1024) break;
        }
        return total;
    }

    public void Dispose()
    {
        if (_pView != IntPtr.Zero) { UnmapViewOfFile(_pView); _pView = IntPtr.Zero; }
        if (_hMap != IntPtr.Zero) { CloseHandle(_hMap); _hMap = IntPtr.Zero; }
        _ok = false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenFileMapping(uint desiredAccess, bool inheritHandle, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr MapViewOfFile(IntPtr hFileMappingObject, uint desiredAccess, uint fileOffsetHigh, uint fileOffsetLow, uint numberOfBytesToMap);

    [DllImport("kernel32.dll")]
    private static extern bool UnmapViewOfFile(IntPtr lpBaseAddress);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll")]
    private static extern int VirtualQuery(IntPtr lpAddress, ref MEMORY_BASIC_INFORMATION lpBuffer, uint dwLength);
}
