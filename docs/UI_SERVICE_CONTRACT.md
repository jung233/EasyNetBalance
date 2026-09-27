# EasyNetBalance UI ↔ Service 对接文档

本文记录 Tauri UI 与 EasyNetBalance Windows Service 之间的稳定接口。请求和响应仍通过本机命名管道传递；Rust 后端的 DTO、方法分派和序列化代码位于 `src/EasyBalance.UI/src-tauri/src`。修改接口时应同步更新本文和前端类型。

## 传输与错误

- 本机命名管道：`EasyBalance.Control.v1`，双向字节流，UTF-8，无 BOM。客户端每次连接发送**一行 JSON 加换行**，服务端返回一行 JSON；单次请求上限 1 MiB，服务端读请求超时 10 秒，响应上限 64 MiB。UI 对遥测请求等待 8 秒，对其他请求等待 45 秒；服务端不单独限制响应生成时间。服务端一次处理一个连接。不要把管道响应当作流式推送。
- 请求：`{"method":"GetStatus","payload":null}`。`payload` 可省略；请求和响应属性采用 camelCase。枚举以字符串表示，时间戳为 ISO 8601；持续时间沿用兼容的 `HH:mm:ss[.fffffff]` 字符串格式。
- 响应：`{"success":true,"error":null,"payload":{...}}`。必须先判断 `success`；失败时显示 `error`，不要静默吞掉。失败后重新读取 `GetStatus` 和日志，以免界面显示旧状态。
- UI 可每 2 秒轮询一次 `GetConnectionTelemetry` 计算实时速率；离开监控页时停止轮询。`GetStatus`、`GetAdapters` 等用于首次加载和用户刷新。请求按上述期限等待，并处理服务不可用。

## 只读方法

| method | payload 内容 | 成功响应的 payload |
| --- | --- | --- |
| `GetStatus` | 无 | `routingEnabled`, `coreRunning`, `coreFaulted`, `version`, `uptime`, `lastError`, `lastExitCode`, `policies[]`；策略状态有 `policyId`, `name`, `activeIPv4Interface`, `activeIPv6Interface`, `noHealthyIPv4Interface`, `noHealthyIPv6Interface`。`routingEnabled` 是保存的开关，需结合 `coreRunning` 判断实际运行。 |
| `GetCapabilities` | 无 | `{ "supportsTrafficRatio": true, "supportsProcessRules": true, "supportsTun": true, "core": "mihomo" }`，用于显示当前服务和核心能力。 |
| `GetAdapters` | 无 | `NetworkAdapterInfo[]`：`id` 是持久网卡 GUID；`name`, `description`, `networkInterfaceType`, `operationalStatus`, `speed`, `ipv4Addresses`, `ipv6Addresses`, `ipv4Gateways`, `ipv6Gateways`, `dnsServers`, `isPhysical`, `isVirtual`, `isUserAllowed`, `ipv4Health`, `ipv6Health`, `ipv4Latency`, `ipv6Latency`, `lastProbeTime`, `lastStateChange`。属性大小写以实际 JSON 为准，客户端建议大小写不敏感。 |
| `GetSettings` | 无 | `AppSettings`，含 `defaultPolicy`, `policies`, `applicationRules`, `probeEndpoints`, 探测间隔、`ipv6Enabled`, `strictRoute` 等。更新设置时保留未知字段。 |
| `GetPolicies` | 无 | `RoutingPolicy[]`，第一个为默认策略。`id`, `name`, `primaryInterfaceId`, `fallbackInterfaceId`, `loadBalanceEnabled`, `primaryTrafficPercent`, `failoverEnabled`, `autoFailback`, `enabled`, 健康阈值等。 |
| `GetRules` | 无 | `ApplicationRule[]`，进程路径或名称、`policyId`, `priority`, `enabled` 等。 |
| `GetLogs` | 无 | `RuntimeLogEntry[]`：`timestamp`, `level`, `source`, `message`。 |
| `GetDiagnostics` | 无 | `RuntimeDiagnostics`：探测、切换和 Service、core、UI 资源统计。 |
| `GetProcesses` | 无 | `ProcessSnapshot[]`：`processName`, `executablePath`, `pids`。 |
| `GetGeneratedConfig` | 无 | `{ "json": "..." }`；`json` 是为兼容现有 UI 保留的字段名，内容为 Mihomo YAML；API secret 和代理密码已遮盖。 |
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
| `SetTrafficRatio` | `{ "primaryTrafficPercent": 60 }` | 0–100 的整数；只更新默认策略目标并持久化，服务原地调整运行中的双 WAN 代理权重，不重启 Mihomo。滑杆使用约 300–500 ms 防抖，并在失败时显示错误。未开启负载时可预存比例。 |
| `SetDefaultPolicy` | `{ "id": "策略-ID" }` | 切换默认策略。 |
| `DeletePolicy`, `DeleteRule` | `{ "id": "ID" }` | 删除对应对象，服务端会校验引用关系。 |
| `SaveRule` | 完整 `ApplicationRule` 对象 | 保存或更新应用规则。 |
| `SaveSettings` | 完整 `AppSettings` 对象 | 保留已有字段；可能重生成配置。 |
| `SetInterfaceUsability` | `{ "interfaceId": "网卡-GUID", "allowed": true }` | 修改网卡可用性。 |
| `RestartCore`, `ValidateConfig` | 无 | 用户主动发起的维护操作。 |
| `TestInterface` | `{ "interfaceId": "网卡-GUID", "family": "IPv4" }` | `family` 为 `IPv4` 或 `IPv6`；返回 `interfaceId`, `family`, `health`, `latencyMs`。 |
| `ExportDiagnostics` | `{ "redactIpAddresses": true }` | 返回 `{ "fileName": "...zip", "dataBase64": "..." }`；由 UI 让用户选择保存位置。 |

## Windows 安装与更新约定

发布包是一个 Windows x64 NSIS 安装器 EXE，内含 Tauri UI、同一个 `EasyNetBalance.exe` 服务宿主、从固定 Mihomo 源码修补并编译的核心、匹配源码归档和许可证/来源材料。安装器请求管理员权限，把应用安装到 `%ProgramFiles%\EasyNetBalance`，将 `EasyNetBalance.exe --service` 注册为自动启动的 `EasyNetBalance` Windows Service，并把核心安装为 `%ProgramFiles%\EasyNetBalance\core\mihomo.exe`。核心目录和 `%ProgramData%\EasyNetBalance` 数据目录仅授予 SYSTEM 和 Administrators 写入权限。

升级前安装器先停止当前的 `EasyNetBalance` 服务；若检测到旧版 `EasyBalance` 服务，也会停止并移除旧 SCM 注册，再注册新版服务并启动。卸载时停止并移除 `EasyNetBalance` 服务，但保留 `%ProgramData%` 下的新旧数据目录。新设置、生成配置、日志和 Mihomo 状态保存在 `%ProgramData%\EasyNetBalance`；首次启动时仅当新 `settings.json` 不存在，服务才从旧版 `%ProgramData%\EasyBalance\settings.json` 复制设置文件，旧目录始终保留。普通 UI 通过 `EasyBalance.Control.v1` 管理服务，关闭窗口后服务继续运行。安装或升级失败必须在安装器中显示可操作错误。不要在 UI 进程直接启动 Mihomo，也不要把 API secret、代理密码或未遮盖配置写入普通日志。
