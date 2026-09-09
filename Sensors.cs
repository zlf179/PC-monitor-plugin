using LibreHardwareMonitor.Hardware;

namespace PcMonitor;

/// <summary>
/// LibreHardwareMonitor 传感器封装：CPU/GPU 占用、温度、频率、电压。
/// 需要管理员权限运行（读 MSR/EC）。
/// 传感器名按实测适配：13 代酷睿时钟为 "P-Core #1"；N 卡功耗为 "GPU Package"；
/// 13 代 CPU 无核心电压传感器，从主板 SuperIO 的 VCORE 兜底读取。
/// </summary>
public sealed class Sensors : IDisposable
{
    private readonly Computer _computer = new()
    {
        IsCpuEnabled = true,
        IsGpuEnabled = true,
        IsMemoryEnabled = true,
        IsMotherboardEnabled = true,   // 主板 SuperIO：VCORE / Bus Clock 兜底
    };

    public float CpuLoad, CpuTemp, CpuVoltage, CpuClock, CpuPower;
    public float GpuLoad, GpuTemp, GpuCoreClock, GpuMemUsed, GpuPower;
    public float RamUsed;

    public void Start()
    {
        _computer.Open();
        foreach (var hw in _computer.Hardware)
        {
            hw.Update();
            foreach (var sub in hw.SubHardware) sub.Update();
        }
    }

    public void Update()
    {
        foreach (var hw in _computer.Hardware)
        {
            hw.Update();
            HandleHardware(hw);
            // 主板子硬件（SuperIO 芯片）里读电压/时钟
            foreach (var sub in hw.SubHardware)
            {
                sub.Update();
                HandleHardware(sub, hw.HardwareType == HardwareType.Motherboard);
            }
        }
    }

    private void HandleHardware(IHardware hw, bool isMotherboard = false)
    {
        bool isCpu = hw.HardwareType == HardwareType.Cpu;
        bool isGpu = hw.HardwareType == HardwareType.GpuNvidia
                  || hw.HardwareType == HardwareType.GpuAmd
                  || hw.HardwareType == HardwareType.GpuIntel;

        foreach (var s in hw.Sensors)
        {
            if (s.Value == null) continue;
            float v = s.Value.Value;
            switch (s.SensorType)
            {
                case SensorType.Load:
                    if (s.Name == "CPU Total" && isCpu) CpuLoad = v;
                    else if (s.Name == "GPU Core" && isGpu) GpuLoad = v;
                    else if (s.Name == "GPU Memory" && isGpu) GpuMemUsed = v;
                    else if (s.Name == "Memory" && hw.HardwareType == HardwareType.Memory) RamUsed = v;
                    break;
                case SensorType.Temperature:
                    if (s.Name.Contains("Package") && isCpu) CpuTemp = v;
                    else if (isGpu && (s.Name == "GPU Core" || s.Name.Contains("Core") || s.Name == "GPU")) GpuTemp = v;
                    break;
                case SensorType.Clock:
                    // 13 代酷睿 P 核叫 "P-Core #1"（老平台 "CPU Core #1"）
                    if (isCpu && (s.Name == "CPU Core #1" || s.Name == "P-Core #1")) CpuClock = v;
                    else if (s.Name == "GPU Core" && isGpu) GpuCoreClock = v;
                    else if (isMotherboard && (s.Name == "CPU Core" || s.Name == "Bus Clock") && CpuClock == 0) CpuClock = v;
                    break;
                case SensorType.Voltage:
                    // 13 代：MSR 电压传感器在 CPU 硬件下，名 "CPU Core"（PawnIO 驱动后可用）
                    if (isCpu && s.Name == "CPU Core") CpuVoltage = v;
                    // 兜底：主板 SuperIO VCORE（部分板子分压系数不对，仅 CPU 无数据时用）
                    else if (isMotherboard && s.Name.Contains("Vcore") && CpuVoltage == 0) CpuVoltage = v;
                    break;
                case SensorType.Power:
                    // CPU 封装功耗；N 卡功耗 "GPU Package"（A 卡 "Power"/"GPU PPT"）
                    if (isCpu && s.Name.Contains("Package")) CpuPower = v;
                    else if (isGpu && (s.Name.Contains("Package") || s.Name.Contains("Power") || s.Name.Contains("PPT"))) GpuPower = v;
                    break;
            }
        }
    }

    public void Dispose() => _computer.Close();
}
