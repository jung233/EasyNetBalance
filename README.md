# EasyBalance

EasyBalance 是 Windows 桌面应用，使用 sing-box TUN 按进程为不同网卡选择出口。Windows Service 负责网卡发现、IPv4/IPv6 独立健康检查、故障转移和 sing-box 生命周期；WPF 界面通过本机 Named Pipe 管理服务。关闭界面不会停止路由。

## 状态

当前仓库提供 MVP 源码、Solution、单元测试和可选集成测试。按本次开发约束，**本地尚未运行 build、测试、sing-box 配置校验或真实网卡故障转移**。本机目前只有 .NET 8 运行时，没有 .NET SDK。GitHub Actions 在 Windows 上编译解决方案并发布预览包，但不执行测试；测试须等用户批准。对选定的 sing-box.exe 仍需在目标机器实际执行配置检查和连通性验证。

## 结构

| 路径 | 作用 |
| --- | --- |
| `src/EasyBalance.Shared` | 配置、规则、策略、网卡模型、JSON 持久化及 IPC DTO |
| `src/EasyBalance.Service` | Windows Service、健康调度、故障转移、sing-box 控制面 |
| `src/EasyBalance.UI` | WPF 管理界面 |
| `tests/EasyBalance.Tests` | 纯内存状态机测试 |
| `tests/EasyBalance.IntegrationTests` | 可选 sing-box CLI 集成测试 |
| `sing-box/` | 本地参考用的上游源码树（Git 忽略）；不是发布包内容 |

数据平面完全由 sing-box 提供。Service 生成 TUN、direct outbound、每策略每地址族一个 selector，以及 `process_path`/`process_name` 路由规则。网卡持久 ID 是 `NetworkInterface.Id` GUID；生成配置时才解析当前网卡名称作为 `bind_interface`。同策略应用共用 selector。健康状态按网卡和地址族共享，正常 failover 只调用本机控制 API，不重启 sing-box。新连接走新网卡；已有 TCP 连接可能中断，由应用重连。

## 要求与构建

- Windows 10/11，.NET 8 SDK（构建时）和管理员权限（安装服务及 TUN）。
- 带 `with_clash_api` 构建标记的兼容 sing-box Windows 二进制。Service 会运行 `sing-box version` 检测实际能力；不在配置中钉死某个发行版本。
- GitHub Release 包已将官方 `sing-box.exe` 放在 Service 目录的 `core\sing-box.exe`，安装该包无需另外下载核心或 .NET 运行时。Service 与 UI 使用压缩的单文件自包含发布，保留各自目录以确保双击 UI 能正常启动。自行从源码构建时，可将兼容二进制放在 Service 可执行文件旁的 `core\sing-box.exe`，或在 Advanced 中设置管理员保护目录下的完整路径。仅管理员应替换二进制。可选的本地 `sing-box/` 源码树不等同于 `core/sing-box.exe`。

GitHub Actions 在 `main` 更新时创建预览 Release，捆绑未修改的上游 sing-box Windows 二进制，并在同一 Release 提供对应源码包及许可文本。归属与再分发说明见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

```powershell
dotnet build .\EasyBalance.sln -c Release
dotnet test .\tests\EasyBalance.Tests\EasyBalance.Tests.csproj -c Release
```

本次开发遵照用户要求不运行以上命令；它们是批准测试后的操作指引。

## 运行

调试时先在提升权限的终端运行 Service，然后以普通用户启动 UI：

```powershell
.\src\EasyBalance.Service\bin\Release\net8.0-windows\EasyBalance.Service.exe --console
.\src\EasyBalance.UI\bin\Release\net8.0-windows\EasyBalance.UI.exe
```

安装服务时，将发布包中的所有文件一起放入固定目录，不要拆开共用运行时文件，再在提升权限的终端运行：

```powershell
sc.exe create EasyBalance binPath= "C:\Program Files\EasyBalance\EasyBalance.Service.exe" start= auto
sc.exe start EasyBalance
```

卸载：

```powershell
sc.exe stop EasyBalance
sc.exe delete EasyBalance
```

服务配置位于 `%ProgramData%\EasyBalance\settings.json`，生成配置位于 `generated\sing-box.json`。配置写入采用同目录临时文件和原子替换。UI 可用 Named Pipe 管理规则；UI 不直接启动 sing-box。关闭 UI 后，服务和 sing-box 继续运行。默认不保留托盘进程。

## 使用与故障转移

在 Interfaces 页面选择可用网卡；在 Failover/Policies 中建立主网卡和备用网卡，设置默认策略；在 Application Rules 中选当前进程或浏览 EXE，指定策略。完整路径优先于同名进程。不存在的 EXE 规则保留，便于以后重新安装。

验证 Ethernet → Wi-Fi：准备两张均可联网的网卡，策略主网卡选 Ethernet、备用选 Wi-Fi，启动 Routing；确认 Dashboard 中两个地址族都健康；断开 Ethernet 并观察 IPv4/IPv6 selector 和日志。IPv4-only：只让 Ethernet 的 IPv4 失效，确认 IPv4 切 Wi-Fi 而 IPv6 保持 Ethernet。IPv6-only：只让 Ethernet 的 IPv6 失效，确认 IPv6 切 Wi-Fi 而 IPv4 保持 Ethernet。恢复主网卡后，若开启 AutoFailback，连续成功、稳定期和最短保持时间均满足后回切。

Service 使用绑定指定网卡和地址族的 HTTPS socket probe，稳定时低频轮换 endpoint；首次失败追加探测，Down 后较快探测恢复。Windows 网络变化事件会合并后刷新网卡。不要仅凭网卡 Link Up 判断 Internet 可用。

配置字段依据 sing-box 官方 [TUN](https://sing-box.sagernet.org/configuration/inbound/tun/)、[路由规则](https://sing-box.sagernet.org/configuration/route/rule/)、[selector](https://sing-box.sagernet.org/configuration/outbound/selector/) 和 [Clash API](https://sing-box.sagernet.org/configuration/experimental/clash-api/) 文档。最终是否支持仍由本机所选二进制的版本、构建标记及 `sing-box check` 决定。

## 诊断与排障

Dashboard 和 Diagnostics 显示核心状态、网卡、路由与探测计数；Logs 显示最近事件。Advanced 可校验生成配置和导出本地诊断 ZIP。勾选 IP 遮盖时，导出包会遮盖网卡地址并省略可能包含 IP 的日志、设置和生成配置。控制 API 只监听 `127.0.0.1`，使用随机 secret。UI 读取的生成配置会遮盖 secret；诊断包不包含未遮盖的生成配置。不会上传遥测。

如果服务显示 core faulted，先检查 sing-box 路径、Windows Service 管理员权限、`with_clash_api` 构建标记、TUN 驱动和最近日志。若互联网不通或疑似路由环路，检查每个 direct outbound 的 `bind_interface` 是否为当前物理网卡名称，并分别测试 IPv4/IPv6。VPN、Hyper-V、WSL、Docker 等环境可先关闭 strict_route，再逐步排查路由冲突。

## 资源设计与已知限制

单一健康调度循环在空闲时异步等待，不按应用或策略创建探测器，不持续扫描进程。UI 退出后内存归零是指 UI 进程结束，不包括 Service 和 sing-box。CPU、内存和探测规模目标尚未实测。

EasyBalance 不提供已有 TCP 连接跨网卡无缝迁移、单流带宽叠加、MPTCP 或包级多路径聚合。故障转移只改变后续新连接。不同 sing-box 构建可能缺少所需能力；Service 将拒绝不兼容二进制。当前版本的真实 TUN、DNS、IPv6、睡眠唤醒和与其他 VPN 共存仍需在目标 Windows 环境验证。
