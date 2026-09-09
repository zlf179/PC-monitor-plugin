# pc-monitor-pc

配套 [ESP32-PC-monitor](https://github.com/zlf179/ESP32-PC-monitor) 固件的 Windows 桌面客户端（.NET 8 WinForms）：采集本机监控数据，经 USB 串口推送到 ESP32-S3 圆屏设备显示。

## 功能

- **串口自动识别**：按 USB VID `303A`（乐鑫）匹配设备，插拔/开机自动重连，无需手动选口
- **场景自动切换**（设备端页面跟随）：
  - 游戏在前台（RTSS 帧来源 = 前台进程）→ GAME 页
  - 浏览器在前台 → WEB 页（浏览器累计使用时长，开机周期内累计、每分钟持久化）
  - 媒体播放 → MUSIC 页；其余 → 待机页
  - 支持自定义进程规则（客户端界面配置）
- **FPS / 帧时间**：读取 RTSS 共享内存（自动兼容新旧内存布局）但无法获取CPU、GPU数据
- **CPU/GPU 传感器**：LibreHardwareMonitorLib（温度/频率/功耗/电压/占用）
- **媒体控制**：Windows SMTC（设备端按键 → 播放/暂停/上下曲）
- **天气地区下发**：输入城市名（如"成都"），Open-Meteo 地理编码转经纬度后下发设备，设备存 NVS
- **WiFi 配网**：客户端界面下发 SSID/密码到设备（存 NVS，无需重烧固件）
- **开机自启**：计划任务方式（requireAdministrator 程序无法用注册表 Run 键），登录时静默启动进托盘

## 运行要求

- Windows 10 19041+ / .NET 8 运行时
- **以管理员运行**（manifest 已声明：传感器 Ring0 访问、计划任务注册）
- 建议安装 [PawnIO](https://pawnio.com) 驱动 —— LibreHardwareMonitor 经它读取 MSR/SuperIO，否则 CPU 频率/电压/功耗无数据（GPU 经 NVAPI 不受影响）
- FPS 数据需要 [RTSS](https://www.guru3d.com/download/rtss-rivatuner-statistics-server/) 在后台运行

## 构建

```bash
dotnet build -c Release
```

输出：`bin/Release/net8.0-windows10.0.19041.0/PcMonitor.exe`

NuGet 依赖：LibreHardwareMonitorLib 0.9.*（自动还原）

## 源码结构

| 文件 | 职责 |
|---|---|
| MainForm.cs | 主界面、场景判定、数据汇总推送（1s） |
| SerialLink.cs | 串口连接/识别/自动重连（专用读线程） |
| RtssApi.cs | RTSS 共享内存解析（FPS/帧时间） |
| Sensors.cs | LHM 传感器采集（CPU/GPU/内存） |
| ForegroundApp.cs | 前台进程监测（场景判定 + 浏览器时长统计） |
| MediaControl.cs | SMTC 媒体会话控制 |
