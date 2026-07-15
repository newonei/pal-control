# 首次启动运行手册

本手册适用于尚未生成 `Pal/Saved` 和 `Mods/PalModSettings.ini` 的新服务端。首次启动的目标是生成真实配置并确认版本，不是马上启用写模组。

## 启动前

1. 备份当前 `PalServer` 目录或确认可以用 SteamCMD 重装。
2. 使用显式安装路径执行更新，避免现有 SteamCMD 元数据中的旧路径被继续使用：

   ```powershell
   & "C:\SteamCMD\steamcmd.exe" `
     +force_install_dir "C:\Palworld\PalServer" `
     +login anonymous `
     +app_update 2394010 validate `
     +quit
   ```

3. 不要修改 `DefaultPalWorldSettings.ini`；它是示例文件。
4. 首次启动不为 REST/RCON 添加公网防火墙放行或端口映射；Control API 始终通过 loopback 地址访问。Palworld 进程可能显示为 `0.0.0.0` 监听，因此不能把“使用 127.0.0.1 连接”误当成入站隔离。

## 首次启动后

确认生成：

```text
PalServer\Pal\Saved\Config\WindowsServer\PalWorldSettings.ini
PalServer\Pal\Saved\Logs\
PalServer\Mods\PalModSettings.ini
```

然后完成：

1. 为 `AdminPassword` 设置强随机值。
2. 设置 `RESTAPIEnabled=True` 和 `RESTAPIPort=8212`。
3. Windows 防火墙不放行 8212 公网入站；Control API 通过 loopback 访问。
4. 请求官方 `/info`、`/players`、`/metrics` 并保存脱敏响应作为集成测试 fixture。
5. 记录游戏版本、Steam buildid、字段差异和实际日志格式。

## 启用模组

仅在完成 UE4SS/SDK 版本选择和只读 smoke test 后：

1. 把正式 Workshop 包放到 `PalServer\Mods\Workshop\PalControlNative\`。
2. 确认包根目录有经工具生成并校验的 `Info.json`。
3. 确认 `InstallRule` 中服务端规则含 `"IsServer": true`。
4. 在 `PalModSettings.ini` 中添加：

   ```ini
   [PalModSettings]
   bGlobalEnableMod=true
   ActiveModList=PalControlNative
   ```

5. 重启服务端，检查 `Mods\ManagedMods\PalControlNative\InstallManifest.json`。
6. 将 `PalControl.ini` 保持 `ReadOnly=true`，完成 hello、玩家、背包、PalBox 只读验证。

## 开放写入前的门禁

- 当前 game build 已加入兼容矩阵。
- 关键探针全部通过。
- 真实备份已验证可以恢复。
- dry-run diff 与游戏内结果一致。
- 重复 idempotency key 不会重复发物品。
- ACK 丢失测试进入 `uncertain` 并成功对账。
- `inventory.consume` 只有完成真实“扣物 → 保存 → 停服 → 重启 → 重新登录”后才声明 stable；experimental 或 `persistenceVerified=false` 时资源兑换保持关闭。
- 玩家掉线/换 session 返回冲突。
- Pal 不在 Box 中的高风险操作被拒绝。
