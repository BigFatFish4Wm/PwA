# 第三方代码说明

本仓库的 `PwA/Coop/Protocol` 与 `PwA/Coop/Broker` 目录改编自以下项目（MIT License）：

- LocalCoop / STS2CouchCoop — https://github.com/Bahnerbd/STS2CouchCoop
  - Copyright (c) 2026 Bahne
  - MIT License（全文见 https://github.com/Bahnerbd/STS2CouchCoop/blob/local-transport-broker/LICENSE）

改编内容：
- `Protocol/BrokerFrameCodec.cs`、`Protocol/BrokerTransportMessage.cs`、`Protocol/BrokerEnvelope.cs`、
  `Protocol/BrokerClientConnection.cs` 基本沿用其 broker 帧协议与注册握手；
- `Broker/BrokerTcpServer.cs`、`Broker/InMemoryBrokerSession.cs`、`Broker/BrokerClientRegistration.cs`、
  `Broker/BrokerRoute.cs` 沿用其中继实现，去掉了手柄输入协调（ControllerSessionCoordinator）部分；
- `Coop/Transport/*` 与 `Coop/Patches/*` 是新写的：用 broker 实现游戏当前版本（v0.111.0）的
  `NetHost` / `NetClient` 传输，替换 ENet/Steam 主机与加入流程；没有照搬其 `INetGameService` 适配层。

LocalCoop 不包含、也不授权 Slay the Spire 2 的任何资源或二进制；使用需自备正版游戏。
