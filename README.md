# 桌面旁白（Desktop Narrator）

基于 .NET 8 和 WPF 的 Windows 桌面应用，记录应用前台停留与无输入状态，并发现可解释的行为模式。目前为原型，尚未接入 AI 旁白、通知或浏览器标题采集。

## 当前功能

- 托盘运行；深色界面提供当日统计、应用时长扇形图和活动时间线。
- 自动记录应用切换、前台会话、空闲/返回及进程组变化，支持按日期查看本地历史。
- 检测长时间停留、频繁切换、短时间重开、长空闲后返回、多次启动，并展示初筛原因。
- 保留原始证据、评分快照和开发日志；通过初筛不代表已发送旁白。

## 运行

需要 Windows 和 .NET 8 或更高版本的开发工具包。

```powershell
dotnet build .\DesktopNarrator.sln -c Release
dotnet run --project .\src\DesktopNarrator.App\DesktopNarrator.App.csproj -c Release
```

也可在 Visual Studio 中打开 `DesktopNarrator.sln`，将 `DesktopNarrator.App` 设为启动项目。

启动后双击托盘图标，或右键选择“打开桌面足迹”。关闭窗口不会退出程序；请从托盘退出。只运行一个实例，重新编译前先退出旧实例。

## 数据与隐私

- 仅观察当前 Windows 会话的进程身份、前台停留和最后输入时间，不采集截图、窗口/网页标题、正文、网址、剪贴板或输入内容，不上传数据。
- 数据保存在 `%LOCALAPPDATA%\DesktopNarrator\narrator.db`，当前未额外加密；数据库、缓存和编译产物不提交到仓库。
- 统计仅包含已结束且成功保存的会话，包含无输入时段，不等于专注或工作时间。强制结束进程、崩溃或断电可能丢失当前会话及待写数据。

## 验证

```powershell
dotnet run --project .\tests\DesktopNarrator.Tests\DesktopNarrator.Tests.csproj -c Release
```

现有 86 项回归测试，覆盖监测、规则与评分、存储、时间边界及界面。原生监测和界面测试需要交互式 Windows 环境；多显示器缩放和完整托盘交互仍需手工验收。
