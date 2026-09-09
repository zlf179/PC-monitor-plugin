using System.Diagnostics;
using System.IO.Ports;
using System.Text;
using System.Text.Json;

namespace PcMonitor;

/// <summary>
/// USB 串口链路：向 ESP32 推送 JSON 行（与固件 pc_link.c 协议对应）。
/// 协议：{"t":"g","fps":..., ...} 游戏；{"t":"w","clock":...} 时钟；{"t":"i"} 待机
/// 接收用专用读线程（ReadTimeout 短轮询）：不用 DataReceived 事件，
/// SerialPort.Close() 等 DataReceived 回调退出会造成 UI 线程卡顿数秒。
/// </summary>
public sealed class SerialLink : IDisposable
{
    private SerialPort? _port;
    private Thread? _rxThread;
    private volatile bool _closing;
    private readonly string _portName;
    private readonly int _baud;

    public bool Connected => _port?.IsOpen == true && !_closing;

    public event Action<string>? LineReceived;   // ESP32 → PC（hello/hb）

    public SerialLink(string portName, int baud = 115200)
    {
        _portName = portName;
        _baud = baud;
    }

    public void Open()
    {
        Close();
        _closing = false;
        try
        {
            _port = new SerialPort(_portName, _baud)
            {
                ReadTimeout = 200,   // 读线程短轮询节奏；也保证 Close 时线程在 200ms 内退出
                WriteTimeout = 500,
                DtrEnable = true,
                RtsEnable = true,
                Encoding = Encoding.UTF8,
            };
            _port.Open();
            _rxThread = new Thread(RxLoop) { IsBackground = true, Name = "SerialRx" };
            _rxThread.Start();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[serial] 打开 {_portName} 失败: {ex.Message}");
            try { _port?.Dispose(); } catch { }
            _port = null;
        }
    }

    private void RxLoop()
    {
        var port = _port;
        while (!_closing && port?.IsOpen == true)
        {
            string? line;
            try
            {
                line = port.ReadLine();
            }
            catch (TimeoutException)
            {
                continue;
            }
            catch
            {
                break;   // 端口关闭/硬件错误
            }
            if (!_closing && !string.IsNullOrWhiteSpace(line))
                LineReceived?.Invoke(line.Trim());
        }
    }

    public void Close()
    {
        if (_closing && _port == null) return;
        _closing = true;
        try { _port?.Close(); } catch { }   // 关闭使阻塞中的 ReadLine 抛异常 → 读线程自退
        try { _rxThread?.Join(1000); } catch { }
        _rxThread = null;
        try { _port?.Dispose(); } catch { }
        _port = null;
    }

    public void Send(string json)
    {
        if (_port?.IsOpen != true) return;
        try { _port.WriteLine(json); }
        catch { /* 掉线由上层重连 */ }
    }

    public void Dispose() => Close();
}
