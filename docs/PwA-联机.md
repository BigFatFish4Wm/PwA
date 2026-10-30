# PwA 本机双窗口联机（broker 传输）

取代官方 `-fastmp` 回环（那个方案在旧项目里出过状态不同步），改成 CouchCoop 那套
「本机 TCP 中继 + 传输层替换」的架构。

## 结构

```
host 窗口（PWA_COOP=host）
  ├─ 游戏本体：NetHostGameService，但传输换成了 BrokerNetHost
  └─ 本进程内 broker 中继：127.0.0.1:33772（session=pwa）

client 窗口（PWA_COOP=client，index=1）
  └─ 游戏本体：NetClientGameService，传输换成了 BrokerNetClient → 连中继
```

- 握手、消息总线、版本校验仍然由游戏自己的 `NetHostGameService` / `NetClientGameService` 完成，
  我们只实现 `NetHost` / `NetClient` 两个传输类（字节搬运 + 模式/频道）。
- 为什么不用 CouchCoop 的 `INetGameService` 重写：那是 v0.103 的接口形态，v0.111 已经有
  `NetHost` / `NetClient` 这一层抽象，替换传输的代码量小一个数量级。

## 启动

```powershell
# 一键：先起 host（自己开中继），20 秒后起 client
powershell -ExecutionPolicy Bypass -File tools\start-coop.ps1

# 或手动
$env:PWA_COOP='host'; $env:PWA_COOP_ENDPOINT='127.0.0.1:33772'; $env:PWA_COOP_SESSION='pwa'
Start-Process SlayTheSpire2.exe -ArgumentList '--force-steam','off','--clientId','1','-fastmp','host_standard'
# 20 秒后
$env:PWA_COOP='client'; $env:PWA_COOP_INDEX='1'
Start-Process SlayTheSpire2.exe -ArgumentList '--windowed','--force-steam','off','--clientId','1001','-fastmp','join'
```

环境变量：

| 变量 | 含义 |
|---|---|
| `PWA_COOP=host\|client` | 启用联机并指定本窗口角色（不设就是普通单机） |
| `PWA_COOP_INDEX` | 客户端下标，决定传输层 NetId（host 固定 0 → NetId 1） |
| `PWA_COOP_ENDPOINT` | broker 地址，默认 `127.0.0.1:33772` |
| `PWA_COOP_SESSION` | 会话 id，两个窗口必须一致，默认 `pwa` |

## 端口

| 窗口 | 角色 | Mod 端口 |
|---|---|---|
| host | `host` | **8091** |
| client（agent 用） | `client` | **8092** |

World 的自动发现会优先选 client；`sts2_diag` 里能看到 `role` 和 `base`。

## 已实现的关键补丁

| 补丁 | 作用 |
|---|---|
| `BrokerHostPatch` | `NetHostGameService.StartENetHost/StartSteamHost` → 换成 BrokerNetHost |
| `BrokerClientPatch` | `ENetClientConnectionInitializer.Connect` → 换成 BrokerNetClient |
| `BrokerLocalPlayerIdPatch` | 离线模式的「本地玩家 id」与传输层 NetId 对齐（否则客户端认不出自己，房间是空的） |
| `BrokerTransportPump` | 游戏有时不调 `Update`（主机停在角色选择），用主线程每 15ms 主动抽包 |
| `HandshakeKick` | 游戏的握手管理器只登记超时、不写第一包（ENet 传输层替它做了），反射补发一次 |
| `BrokerHandshakeDiagnostics` | 握手链路诊断日志（默认保留，量很小） |

## 调试

每个进程独立日志：`%LOCALAPPDATA%\PwA\coop-<pid>.log`
（游戏自己的 `godot.log` 会被两个窗口轮转覆盖，不能用来排查联机。）

## 已知限制

- 只支持一个 host + 一个 client（多客户端协议上支持，但还没测）。
- 手柄/键鼠分配没做：两个窗口都用键鼠，操作哪个窗口哪个窗口响应。
- 长跑稳定性（多幕、事件、奖励、药水）还在验证中。
