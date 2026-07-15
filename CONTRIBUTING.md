# 贡献指南

感谢参与 Pal Control。这个项目会触及真实游戏存档、玩家身份和双货币账本，因此任何“方便”的写路径都必须保留版本门禁、幂等、回读和 fail-closed 语义。

## 开发环境

- Windows x64；
- Node.js 22、npm 11；
- .NET SDK 10；
- Native 修改还需要 `mods/pal-control-native/dependencies.lock.json` 锁定的 CMake、Rust、MSVC、UE4SS 与 Unreal 提交。

安装并验证 Web/API：

```powershell
npm ci
npm run build
dotnet build .\services\control-api\PalControl.ControlApi.csproj -c Release
npm test
```

Native 构建必须使用锁定入口：

```powershell
.\mods\pal-control-native\scripts\Build-PalControlNative.ps1 `
  -Ue4ssRoot <锁定提交的-UE4SS-目录>
```

脚本不会下载依赖、部署 DLL 或重启真实服务器。没有固定版本的真实测试环境时，不要把 experimental 能力改为 stable。

## 修改规则

- 先阅读 [架构说明](docs/architecture/overview.md)、[方案 A ADR](docs/architecture/decisions/0001-weekly-world-resource-economy.md) 和 [TODO](TODO.md)。
- API 变更同步修改 OpenAPI；Native 消息变更同步修改 JSON Schema 和对应契约文档。
- 所有经济写操作必须带稳定幂等键；`uncertain` 不能自动当作失败退款或盲目重试。
- 不允许新增任意 RCON、任意反射/函数调用、原始内存读写或浏览器直连内部服务。
- 不要用 mock 成功替代真实保存/停服/重启/重新登录证据，也不要在未完成时勾选 TODO。
- 保留与任务无关的工作树改动，不提交生成产物。

## 数据与秘密

绝不提交真实存档、数据库、日志、玩家标识、服务器路径、域名、密码、Token、Cookie、证书、第三方二进制或生成的资源目录。提交前至少运行：

```powershell
git diff --check
git status --ignored --short
git diff --cached --name-only
gitleaks git --staged --redact --no-banner .
```

`.gitleaks.toml` 只对明确列出的测试文件与确定性 fixture 值做“路径 + 精确值”联合例外；不要为了让 CI 变绿而放宽到整个测试目录或关闭默认规则。

只有 README 引用、位于 `docs/images/` 且人工确认脱敏的产品界面截图可以提交。完整矩阵见 [开源发布方案](docs/open-source-release.md)。

## Pull Request

PR 请说明：

- 问题、方案及没有采用的关键替代方案；
- 受影响的安全/经济边界；
- 实际运行的测试命令与结果；
- 配置、数据库或契约迁移和回滚方式；
- 真实联调尚未完成的部分。

涉及漏洞时不要创建公开 PR，先按 [安全策略](SECURITY.md) 私下报告。
