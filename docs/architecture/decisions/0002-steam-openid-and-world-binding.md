# ADR-0002：公网 Steam 服使用 OpenID，当前周角色继续游戏内验证

- 状态：已接受
- 决策日期：2026-07-15
- 决策人：项目维护者
- 关联路线图：[玩法 TODO](../../../TODO.md)
- 权威资料：[Steamworks 用户认证与所有权](https://partner.steamgames.com/doc/features/auth?l=schinese)

## 背景

当前玩家门户允许玩家输入平台 UserId/SteamID64，再向同一在线角色发送一次性验证码。这能证明访问者控制当前游戏角色，但“客户端填写的 SteamID”本身不是网站平台身份证明。

Steam 官方支持网页使用 OpenID 2.0 获得经 Steam 验证的 64 位 SteamID，OP 端点为 `https://steamcommunity.com/openid/`，成功回应的 Claimed ID 格式为 `http://steamcommunity.com/openid/id/<steamid>`。OpenID 只证明网页 Steam 身份，不自动证明它就是当前 Palworld 世界中的某个 `PlayerUID`。

## 决策

1. **公网 Steam 服**必须先用 Steam OpenID 建立平台身份，再用已有游戏内一次性验证码完成该 SteamID 与当前 `worldId + PlayerUID` 的首次绑定或新周重绑。
2. 服务端只信任向 Steam OP 端点执行 `check_authentication` 后得到的有效断言；不信任查询字符串、前端字段、昵称或未校验的 claimed ID。
3. 回调必须使用 HTTPS，`realm` 和 `return_to` 必须来自固定允许列表；使用一次性、有时效的 state 抵御 login CSRF，校验精确 OP endpoint、namespace、identity/claimed_id 一致性、SteamID64 格式和 `response_nonce` 重放。
4. OpenID 成功后只创建“待绑定平台会话”；首次绑定或世界变化前，不允许任何购买、发货、报价或兑换写入。
5. OpenID 会话与玩家会话采用 HttpOnly/Secure/SameSite Cookie，验证成功时轮换 session ID；登录开始、回调、游戏验证码和绑定都使用独立限流、脱敏审计和失败上限。
6. **可信好友服/本机开发服**可以通过显式配置使用现有游戏内验证码作为唯一登录；该模式不得在文档或界面中宣称为公网 Steam 身份验证。
7. 非 Steam 平台暂时使用游戏内验证码 fallback；引入其他官方身份提供方时必须另建 ADR。

## 不在本决策中做的事

- 不要求玩家在幻兽商域页面输入 Steam 密码；凭据只能输入 Steam 官方页面。
- 不把 OpenID 当作 Palworld 应用所有权证明。当前社区服已通过实时在线角色和游戏内验证建立角色控制证明；如以后要求额外所有权检查，再评估需要 Publisher Web API Key 的 Steamworks API。
- 不允许管理员仅凭昵称、截图或用户提交的 SteamID 手工合并钱包。

## 结果

公网部署多了一次 Steam 跳转，但平台身份不再由用户自行声明。游戏内验证码仍然必要，因为它证明当前在线角色与本周 `PlayerUID` 的控制权。两层证明分开后，Steam 网页会话、跨周账户和当前世界角色绑定的边界更明确。

## 实现状态

已实现 `TrustedGameCode`（默认，仅可信服）与 `OpenIdThenGameCode` 两种显式模式。公网/Production 启用玩家门户时必须使用后者：回调只接受固定 Steam OP，服务端执行有超时和响应上限的 `check_authentication`，并验证一次性 state、精确 HTTPS realm/return_to、namespace、`op_endpoint`、`claimed_id = identity`、17 位 SteamID64、签名字段与有时效的不可重放 nonce。OpenID 成功只建立进程内待绑定身份；随后游戏内验证码必须实时落地当前 `worldId + PlayerUID` 绑定，才会轮换并签发正式玩家会话。服务重启会使 state、nonce 关联、待绑定身份和正式会话全部失效。
