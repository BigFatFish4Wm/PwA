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

## 三人房 / 远程玩家（1.2.0+）

默认只监听 `127.0.0.1`。要让别人从别的机器连进来，主机设两个环境变量：

```powershell
# 主机窗口（自己开中继，并监听所有网卡）
PWA_COOP=host
PWA_COOP_BIND=0.0.0.0
PWA_COOP_TOKEN=自己定一个口令
PWA_COOP_SESSION=pwa
```

一键脚本：

```powershell
powershell -ExecutionPolicy Bypass -File tools\start-coop.ps1 -Bind 0.0.0.0 -Token '<口令>' -Clients 1
```

`-Clients 1` = 本地只开汐那一个 client；第三人是远程玩家。

### 远程玩家的机器

需要：Windows + 《杀戮尖塔 2》v0.111.0 + 安装同一个 PwA Mod（发布包里的 `PwABridge-*.zip`）。

```bat
set PWA_COOP=client
set PWA_COOP_INDEX=2
set PWA_COOP_ENDPOINT=<你的IP>:33772
set PWA_COOP_TOKEN=<同一个口令>
SlayTheSpire2.exe --force-steam off --clientId 1002 -fastmp join
```

- `PWA_COOP_INDEX` 必须和本地 client 不重复：本机汐是 1，远程用 2（再来一个用 3）；
- 远程玩家的 Mod 端口会变成 8094（client 角色 8092 被本地汐占用后顺延），不影响游戏；
- 三个窗口都在「角色选择」后各自点「启程」，主机开始，游戏内最多 4 人。

### 网络（任选一种 TCP 通路，不限于 Tailscale）

broker 就是一条普通 TCP 长连接，只要远程能 `telnet <host> 33772`，就能进：

| 方式 | 做法 | 适合 |
|---|---|---|
| Tailscale / ZeroTier | 两边装，远程填主机虚拟 IP | 最省事、加密、免端口 |
| 内网穿透（frp / ngrok / cpolar / 花生壳） | 主机跑隧道客户端，把 `127.0.0.1:33772` 映射成公网 `host:port`；远程填公网地址 | 没有公网 IP 也能用 |
| 路由器端口转发 | 转发 TCP 33772 到主机，远程填公网 IP | 有公网 IP / IPv4 |
| SSH 反向隧道 | 有 VPS 时 `ssh -R` | 已有服务器 |

内网穿透要点：

- **必须是 TCP 隧道**，不是 HTTP 隧道（`ngrok tcp 33772`、frp 的 `type = "tcp"`、`cpolar tcp 33772`）；
- 用穿透时主机可以继续 `PWA_COOP_BIND=127.0.0.1`（隧道客户端连本机回环），不必对外绑 `0.0.0.0`，更安全；
- 远程的 `PWA_COOP_ENDPOINT` 填隧道给出的 `主机:端口`，`PWA_COOP_TOKEN` 仍要一致；
- 免费隧道常有带宽/时长限制，掉线重连即可，游戏是回合制，几百毫秒延迟可以接受；
- IPv6 字面量地址（带冒号）目前不支持，需用域名或 IPv4。

公网暴露时**必须设 `PWA_COOP_TOKEN`**：中继会拒绝没有口令或口令不匹配的注册（实测返回 `token mismatch`）。

### 已知限制

- 远程玩家必须装 Mod（走 broker）；原版客户端不能直接进（那需要 ENet↔broker 桥，未做）；
- broker 是明文 TCP，公网请走 Tailscale 或 SSH 隧道，别裸奔；
- 3 人房只在本机模拟过（host + 两个 client，其中一个用局域网 IP 走中继），真实异地延迟尚未实测。

## UI 里的三种角色（1.3.0+）

Coopanion 的「联机房间」面板现在按角色分开：

| 位置 | 谁 | 占不占本机资源 |
|---|---|---|
| host（8091） | 你，固定本机 | 1 个游戏进程 |
| client（8092） | 汐，固定本机 | 1 个游戏进程 |
| 远程位 0–2 | 别人，在**他自己的电脑**上开窗口 | **不占**本机进程/内存 |

- 远程位只决定：发什么口令、生成什么样的启动命令、面板显示「已连 n/2」；
- 远程位的窗口**不在你机器上启动**，所以开 1 个还是 2 个远程位，本机都只有 host + 汐两个进程；
- 只有勾「本机模拟远程」（`localClients=2`）才会多开第三个本机窗口，用于自己测试三人局。

面板上的「复制远程命令」会生成对方的启动脚本（含 index=2 和口令）。