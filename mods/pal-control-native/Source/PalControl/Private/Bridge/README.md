# Bridge implementation checklist

- Named Pipe ACL 只允许运行 PalServer 与 Control API 的 Windows 身份。
- frame length 先校验，再分配内存；超过 `MaxFrameBytes` 立即断开。
- JSON schema 校验、命令 deadline 和签名/会话校验均在入队前完成。
- 队列满时返回 `BRIDGE_BACKPRESSURE`，不得无限增长。
- Game Tick 依据 `MaxCommandsPerTick` 消费，重新查找 player/container/Pal。
- 每次写入先校验 session、revision、容量、stack、物品目录和 Pal 状态。
- 只返回复制后的 DTO；绝不把 UObject 指针传回 IPC 线程。
