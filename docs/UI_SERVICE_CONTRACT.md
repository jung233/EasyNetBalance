# EasyBalance UI ↔ Service 对接文档

本文记录当前 Windows Service 的接口，供从零重写的 UI 使用。实际模型定义在 `src/EasyBalance.Shared/Models.cs`，运行时响应定义在 `src/EasyBalance.Service/RuntimeDtos.cs`；修改接口时应同步更新本文。

## 传输与错误

- 本机命名管道：`EasyBalance.Control.v1`，双向字节流，UTF-8，无 BOM。客户端每次连接发送**一行 JSON 加换行**，服务端返回一行 JSON；单次请求上限 1 MiB，读请求超时 10 秒。服务端一次处理一个连接。不要把管道响应当作流式推送。
- 请求：`{"method":"GetStatus","payload":null}`。`payload` 可省略；请求和响应属性采用 camelCase。枚举以字符串表示，时间戳为 ISO 8601，`TimeSpan` 为 .NET JSON 时间格式。
- 响应：`{"success":true,"error":null,"payload":{...}}`。必须先判断 `success`；失败时显示 `error`，不要静默吞掉。失败后重新读取 `GetStatus` 和日志，以免界面显示旧状态。
- UI 可每 2 秒轮询一次 `GetConnectionTelemetry` 计算实时速率；离开监控页时停止轮询。`GetStatus`、`GetAdapters` 等用于首次加载和用户刷新。请求应设置超时并处理服务不可用。

## 只读方法

| method | payload 内容 | 成功响应的 payload |
| --- | --- | --- |
| `GetStatus` | 无 | `routingEnabled`, `coreRunning`, `coreFaulted`, `version`, `uptime`, `lastError`, `lastExitCode`, `policies[]`；策略状态有 `policyId`, `name`, `activeIPv4Interface`, `activeIPv6Interface`, `noHealthyIPv4Interface`, `noHealthyIPv6Interface`。`routingEnabled` 是保存的开关，需结合 `coreRunning` 判断实际运行。 |
| `GetAdapters` | 无 | `NetworkAdapterInfo[]`：`id` 是持久网卡 GUID；`name`, `description`, `networkInterfaceType`, `operationalStatus`, `speed`, `ipv4Addresses`, `ipv6Addresses`, `ipv4Gateways`, `ipv6Gateways`, `dnsServers`, `isPhysical`, `isVirtual`, `isUserAllowed`, `ipv4Health`, `ipv6Health`, `ipv4Latency`, `ipv6Latency`, `lastProbeTime`, `lastStateChange`。属性大小写以实际 JSON 为准，客户端建议大小写不敏感。 |
| `GetSettings` | 无 | `AppSettings`，含 `defaultPolicy`, `policies`, `applicationRules`, `probeEndpoints`, 探测间隔、`ipv6Enabled`, `strictRoute` 等。更新设置时保留未知字段。 |
| `GetPolicies` | 无 | `RoutingPolicy[]`，第一个为默认策略。`id`, `name`, `primaryInterfaceId`, `fallbackInterfaceId`, `loadBalanceEnabled`, `primaryTrafficPercent`, `failoverEnabled`, `autoFailback`, `enabled`, 健康阈值等。 |
| `GetRules` | 无 | `ApplicationRule[]`，进程路径或名称、`policyId`, `priority`, `enabled` 等。 |
| `GetLogs` | 无 | `RuntimeLogEntry[]`：`timestamp`, `level`, `source`, `message`。 |
| `GetDiagnostics` | 无 | `RuntimeDiagnostics`：探测、切换和 Service、core、UI 资源统计。 |
| `GetProcesses` | 无 | `ProcessSnapshot[]`：`processName`, `executablePath`, `pids`。 |
| `GetGeneratedConfig` | 无 | `{ "json": "..." }`；API secret 和代理密码已遮盖。 |
| `GetConnectionTelemetry` | 无 | 下述实时遥测对象。 |

`GetConnectionTelemetry` 示例（字段名称以此为准，值仅作示意）：

```json
{
  "sampledAt": "2026-09-26T11:00:00Z",
  "lastError": null,
  "uploadTotal": 120000,
  "downloadTotal": 450000,
  "outbounds": [
    {
      "interfaceId": "网卡-GUID",
      "name": "WAN A",
      "uploadBytes": 80000,
      "downloadBytes": 300000,
      "activeConnections": 4,
      "targetPercent": 60,
      "available": true,
      "error": null
    }
  ],
  "connections": [
    {
      "id": "连接-ID",
      "process": "browser.exe",
      "processPath": "C:\\Apps\\browser.exe",
      "destinationIp": "203.0.113.10",
      "destinationHost": "example.org",
      "destinationPort": "443",
      "network": "tcp",
      "actualOutbound": "WAN A",
      "predictedOutbound": "WAN A 60% / WAN B 40%",
      "uploadBytes": 1200,
      "downloadBytes": 3400,
      "startedAt": "2026-09-26T10:59:00Z"
    }
  ]
}
```

速率需要用同一 `interfaceId` 的相邻两次 `uploadBytes` / `downloadBytes` 差值除以 `sampledAt` 间隔计算。首次样本、计数器回退和服务重启时显示“采样中”，不要显示伪造的 0。`targetPercent` 是健康状态调整后的有效目标；非平衡出口为 `null`。`actualOutbound` 只能在连接与物理出口能可靠对应时显示名称，否则为 `Unknown`。进程元数据也可能缺失。直连出口计数依赖活动连接采样，短连接可能漏计；双 WAN 代理直接累计经过的载荷字节。单条已建立连接不会因滑杆变化而迁移，比例只影响后续新连接。

## 写入方法

| method | payload 内容 | 说明 |
| --- | --- | --- |
| `EnableRouting`, `DisableRouting` | 无 | 成功后重新读取 `GetStatus`；启用失败应展示服务返回的具体错误。 |
| `SavePolicy` | 完整 `RoutingPolicy` 对象 | 默认策略的负载开关在此设置；开启双 WAN 负载须选择两个不同、允许使用的网卡。此方法可能重生成配置并重启 core，勿在滑杆每一格调用。 |
| `SetTrafficRatio` | `{ "primaryTrafficPercent": 60 }` | 0–100 的整数；只更新默认策略目标并持久化，运行中的双 WAN 代理可原地改权重，不重启 sing-box。滑杆使用约 300–500 ms 防抖，并在失败时显示错误。未开启负载时可预存比例。 |
| `SetDefaultPolicy` | `{ "id": "策略-ID" }` | 切换默认策略。 |
| `DeletePolicy`, `DeleteRule` | `{ "id": "ID" }` | 删除对应对象，服务端会校验引用关系。 |
| `SaveRule` | 完整 `ApplicationRule` 对象 | 保存或更新应用规则。 |
| `SaveSettings` | 完整 `AppSettings` 对象 | 保留已有字段；可能重生成配置。 |
| `SetInterfaceUsability` | `{ "interfaceId": "网卡-GUID", "allowed": true }` | 修改网卡可用性。 |
| `RestartCore`, `ValidateConfig` | 无 | 用户主动发起的维护操作。 |
| `TestInterface` | `{ "interfaceId": "网卡-GUID", "family": "IPv4" }` | `family` 为 `IPv4` 或 `IPv6`；返回 `interfaceId`, `family`, `health`, `latencyMs`。 |
| `ExportDiagnostics` | `{ "redactIpAddresses": true }` | 返回 `{ "fileName": "...zip", "dataBase64": "..." }`；由 UI 让用户选择保存位置。 |

## 单 EXE 安装与更新约定

发布工作流先生成自包含 Service 和官方 `sing-box.exe`，再将两者作为资源嵌入 UI 的单文件 `EasyBalance.exe`。UI 首次启动需提权安装到 `%ProgramFiles%\EasyBalance`，注册并启动 `EasyBalance` Windows Service；后续启动要比较嵌入资源与已安装文件的 SHA-256，版本变化时停服、等待进程退出、替换文件并重新启动。普通 UI 通过管道管理服务，关闭窗口后服务继续运行。入口安装失败必须显示可操作错误，并写 `%LocalAppData%\EasyBalance\logs\ui-startup.log`。不要在 UI 进程直接启动 sing-box，也不要把 API secret、代理密码或未遮盖配置写入普通日志。
