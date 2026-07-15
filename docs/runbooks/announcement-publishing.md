# 公告发布运行手册

公告发布共享基础设施只有在队列和审计都就绪时才会解锁；每个渠道还有独立能力：

```json
{
  "officialRestConnected": true,
  "commandQueueReady": true,
  "auditReady": true,
  "publishAnnouncements": true,
  "publishChatAnnouncements": true,
  "publishClientOverlay": true,
  "bridgeConnected": true
}
```

`publishChatAnnouncements` 依赖官方 REST；`publishClientOverlay` 依赖 Native Bridge 已连接并明确声明 `announcements.overlay.write`。前端只允许选择当前真实可用的渠道。

若新 Native DLL 已构建但 PalServer 仍在运行，浮层会继续显示“能力探针未就绪”。先通过正常保存/关服流程停止 PalServer，再执行 `deploy/windows/activate-client-overlay.ps1`；脚本会校验构建哈希、备份旧 DLL 并安装新 DLL。随后正常启动 PalServer，禁止为了更新 DLL 直接强杀正在服务玩家的进程。

查询地址：

```powershell
$headers = @{ "X-Pal-Admin-Key" = $env:PAL_CONTROL_VIEWER_KEY }
Invoke-RestMethod http://127.0.0.1:5180/api/v1/servers/local/capabilities -Headers $headers
```

`PAL_CONTROL_VIEWER_KEY` 只从密码管理器注入当前受控进程，不能提交到仓库或出现在截图中。

## 持久化目录

默认目录是 `services/control-api/data`，可用 `CommandPersistence:DataDirectory` 修改。运行 Control API 的 Windows 账号必须能创建、追加写入并强制刷新该目录。运行时文件包括：

- `announcement-events.jsonl`：草稿与公告状态；
- `command-audit.jsonl`：命令、幂等索引和追加式审计；
- `command-queue.lock`：单实例独占 lease，防止两个进程同时派发。

不要删除正在使用的事件日志，也不要同时启动两个指向同一目录的 Control API。备份时应先停止 Control API，再复制整个目录。

## 支持范围

当前支持全服公告：`audience.type=global`，`channels` 可为 `chat`、`client-overlay` 或两者。聊天通过官方 REST `POST /announce`；客户端浮层通过 Native Bridge 在游戏线程调用 Palworld 的可靠服务器通知多播，只覆盖派发时在线且与该 GameState 相关的客户端。玩家客户端无需另装 Mod。

公会、指定玩家、网页公告栏和自定义顶部横幅仍会被明确拒绝，不会伪装成发布成功。多渠道命令为每个渠道分别写入 `channel-dispatched` 与终态审计；部分成功会返回逐渠道结果，已经成功或结果不确定的渠道绝不自动重发。

## `uncertain` 处理

若官方 REST 或 Native Bridge 在派发后超时、断线，或 Control API 在派发窗口重启，对应渠道会进入 `uncertain`。系统不会自动重发，以免玩家收到两次公告。多渠道发布时应查看 `result.channels` 以及审计事件的 `channel`/`transport` 字段，先区分已成功、明确失败和结果不确定的渠道。

1. 先在游戏内确认公告是否已经出现；
2. 通过 `GET /api/v1/commands/{commandId}` 查看命令；
3. 通过 `GET /api/v1/audit/commands` 查看 `accepted -> dispatched -> uncertain` 审计；
4. 只有人工确认未送达后，才新建另一条公告。

## 安全验证

下面的测试只连接独立 fake REST 和 fake Native Bridge，不会给真实服务器或真实玩家发公告：

```powershell
.\tests\integration\announcement-publish-smoke.ps1
```
