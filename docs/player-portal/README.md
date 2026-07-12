# 玩家门户部署与安全资料

本目录定义 `apps/player-web/dist` 的公网边界。它只允许玩家门户静态页面和 `/api/v1/player/*`，不把 Control API、运营控制台、Palworld 官方 REST、PalDefender、RCON 或 Native Bridge 暴露给互联网。

当前玩家功能包括本人钱包、商城、订单、流水、撤离记录和撤离地图。撤离地图调用
`GET /api/v1/player/me/extraction-zones`，只从 HttpOnly 会话确定玩家，返回本人的位置与公开的
撤离点配置；不会返回其他玩家位置。页面每 5 秒刷新，位置不可用时明确显示未知而不会推测。

## 文档索引

- [01-部署手册](01-部署手册.md)：构建、DNS、TLS、Windows 服务、发布与回滚。
- [02-端口与信任边界](02-端口与信任边界.md)：公网端口、回环端口和禁止映射清单。
- [03-威胁模型](03-威胁模型.md)：资产、攻击路径、已有缓解与正式上线缺口。
- [04-验收清单](04-验收清单.md)：上线前逐项检查与外部验证命令。
- [Caddyfile 模板](../../deploy/player-portal/Caddyfile)：HTTPS 静态托管与严格反向代理规则。

## 不能混淆的安全结论

1. Caddy 的路径白名单只是网络边界，不是玩家身份认证。`/api/v1/player/*` 仍必须由 Control API 校验登录会话、权限、CSRF、幂等键和资源归属。
2. 当前模板要求 Caddy 与 Control API 在同一台 Windows 主机上，后端只监听 `127.0.0.1:5180`。若拆成两台主机，不能简单把 `5180` 开到公网，必须重新设计受认证的私网或 mTLS 链路。
3. `apps/player-web/dist/index.html` 不存在、Control API 不是回环监听、玩家 API 尚未完成认证，三者任一不满足都不得上线。
4. 运营控制台 `5174` 与玩家门户是两个安全域。不要把运营页面复制进玩家静态目录，也不要给玩家站点增加 `/api/v1/extraction/admin/*`、`/api/v1/servers/*` 等代理规则。
