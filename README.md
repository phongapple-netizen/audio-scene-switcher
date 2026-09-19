# 音频设备切换面板

这是一个面向 Windows 11 的 .NET 10 WPF 原型，把音频输出、音频输入、默认通信端点和摄像头偏好集中到托盘面板。

## 已实现

- 通过 MMDevice API 枚举活动的 Core Audio 输出与输入端点；
- 分别读取 Console、Multimedia、Communications 三种默认角色；
- 通过隔离的 PolicyConfig 兼容层切换默认端点，写入后逐角色回读；
- 场景切换前保存六个角色的快照，中途失败时恢复已经改变的角色；
- 检测切换期间的外部修改，避免回滚覆盖用户或其他程序的新选择；
- 通过 Windows DeviceInformation 枚举摄像头，仅保存摄像头偏好；
- 四个可编辑场景、托盘生命周期、主音量控制和输入峰值电平；
- 设备变化通知与延迟刷新；
- JSON 配置原子写入、损坏文件备份和操作日志；
- 只读设备诊断、可恢复的单角色切换探针和场景事务自测。

## 安全边界

下拉框只编辑当前场景，只有点击“应用选中场景”才会改变系统音频默认角色。摄像头选择是偏好记录，不代表微信、腾讯会议或浏览器已经切换摄像头。

程序只把 Core Audio 状态为 Active 的端点放入日常列表。端点 ID 用于实际切换，友好名称只用于显示。驱动升级或重装可能产生新的端点 ID，此时需要重新选择场景设备。

## 配置和日志

- 配置：`%LOCALAPPDATA%\\AudioPanel\\config.json`
- 日志：`%LOCALAPPDATA%\\AudioPanel\\audio-panel.log`
- 损坏配置备份：`config.json.bad-时间戳.json`

## 构建和验证

```powershell
dotnet build .\\AudioPanel.csproj --configuration Release
dotnet build .\\tools\\AudioPanel.Diagnostics\\AudioPanel.Diagnostics.csproj
dotnet build .\\tools\\AudioPanel.SelfTests\\AudioPanel.SelfTests.csproj
dotnet .\\tools\\AudioPanel.SelfTests\\bin\\Debug\\net10.0-windows10.0.22621.0\\AudioPanel.SelfTests.dll
```

诊断程序默认只读。只有显式传入 `--probe-switch` 时，才会依次临时切换 Multimedia 输出与输入角色并立即恢复。

## 当前限制

- 摄像头没有被所有桌面应用共同遵守的系统默认选择；
- PolicyConfig 的默认端点写入接口不是正式公开 API，需要在 Windows 更新后回归验证；
- 第一版尚未实现单独应用路由、蓝牙连接自动切换和安装程序；
- 当前托盘图标使用系统通用图标。

工程没有第三方 NuGet 依赖。
