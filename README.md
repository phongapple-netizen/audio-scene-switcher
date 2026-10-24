# 音频设备切换面板

这是一个面向 Windows 11 的 .NET 10 WPF 工具，把音频输出、音频输入、默认通信端点和摄像头偏好集中到托盘面板。当前版本为 0.2.1。

## 开机启动

右键托盘图标，点击“开机启动”可开启或关闭当前账户的登录启动。勾选表示当前程序路径已登记。自动启动时只显示托盘图标，不打开面板、不自动应用场景；单击图标可打开面板。手动重复启动会唤起已有窗口。

启动项位于当前账户的 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`，名称为 `AudioPanel`，命令为带引号的程序路径加 `--startup`。无需管理员权限。移动程序后，请在新位置重新开启一次“开机启动”。

## 0.2.0 改进

- 场景载入与用户编辑分开处理，切换编辑场景不会改写通信设备或触发保存；
- 从尝试写入开始跟踪回滚，覆盖写入后抛错、核验失败和最终核验时的外部变化；
- 场景执行结果与设备摘要分别显示，错误提示保留到下一次用户操作；
- 高亮编辑场景，并按六个实际默认角色标记匹配的场景；未配置和离线场景给出明确提醒并阻止应用；
- 音量、静音和输入电平固定显示，系统音量通知同步到界面；初始化和通知更新不会回写音量；
- 只有面板可见且未最小化时采样电平，复用音频接口并在隐藏或设备切换时释放；
- 单击托盘打开面板，菜单显示场景匹配状态；托盘切换成功仅通知，失败时打开面板；
- 每个用户会话只运行一个实例，重复启动唤起现有窗口；退出前等待场景执行和配置保存；
- 配置增加结构检查，保存前冻结数据，损坏配置备份后恢复默认值。

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
- 自绘声波切换图标，窗口与托盘统一使用；
- 托盘菜单支持打开面板、快速切换四个场景和刷新设备；
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
dotnet build .\\tools\\AudioPanel.UiTests\\AudioPanel.UiTests.csproj
dotnet .\\tools\\AudioPanel.SelfTests\\bin\\Debug\\net10.0-windows10.0.22621.0\\AudioPanel.SelfTests.dll
dotnet .\\tools\\AudioPanel.UiTests\\bin\\Debug\\net10.0-windows10.0.22621.0\\AudioPanel.UiTests.dll
```

诊断程序默认只读。只有显式传入 `--probe-switch` 时，才会依次临时切换 Multimedia 输出与输入角色并立即恢复。

事务自测和界面回归使用模拟音频，配置与日志写入临时目录。界面回归需要正常 Windows 用户令牌，以验证当前用户命名管道。验证包括场景选择、编辑保存、离线提示、失败反馈、音量通知、真实 WPF 电平计时器生命周期及单实例激活。

`--smoke` / `--render-smoke` 用于启动和界面渲染验证，不应用任何场景。`--smoke --startup` 还核验登录启动时窗口保持隐藏。验证时通过 `AUDIO_PANEL_DATA_DIRECTORY` 指定隔离的配置和日志目录，通过 `AUDIO_PANEL_SCREENSHOT` 指定 PNG 输出；冒烟模式独立于普通实例锁。自动测试与启动渲染不能替代实际硬件切换、会议软件或听音验证。

## 安装包

双击 `build-installer.cmd` 会按 `AudioPanel.csproj` 的版本号发布自带运行时的单文件程序，并用 Inno Setup 6（`winget install JRSoftware.InnoSetup`）生成 `artifacts\installer\AudioPanel-Setup-<版本>.exe`。安装包按当前用户安装到 `%LOCALAPPDATA%\Programs\AudioPanel`，不需要管理员权限；可选登录后托盘运行和桌面快捷方式。安装和卸载前会结束正在运行的面板；卸载会删除开机启动项，但保留 `%LOCALAPPDATA%\AudioPanel` 中的场景配置和日志。

## 当前限制

- 摄像头没有被所有桌面应用共同遵守的系统默认选择；
- PolicyConfig 的默认端点写入接口不是正式公开 API，需要在 Windows 更新后回归验证；
- 当前版本仍未实现单独应用路由和蓝牙连接自动切换。
- 安装包未做代码签名，首次运行时 Windows SmartScreen 可能提示“未知发布者”。

工程没有第三方 NuGet 依赖。
