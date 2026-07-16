# 管理控制台拆包与体积预算

管理控制台将首屏仪表盘保留在入口包中，其余运营页面通过 `React.lazy` 按导航功能加载。经济运营、运营分析、资源经济、内容版本、玩家中心、奖励管理、地图、公告、存档、服务器配置及接口审计均有独立 JavaScript chunk；页面专属 CSS 也随对应功能加载。首次进入功能页时，`Suspense` 会显示可访问的加载状态。

## 强制预算

预算位于 [`bundle-budget.json`](bundle-budget.json)。`requiredDynamicEntries` 明确列出必须保留为异步入口的运营页面；只要其中任一页面被改回同步导入，构建就会失败。体积字段的单位均为未压缩 KiB（`1 KiB = 1024 bytes`）：

| 指标 | 上限 | 约束目的 |
| --- | ---: | --- |
| 初始 JavaScript 合计 | 250 KiB | 约束入口及其同步依赖，不允许通过新增 vendor chunk 绕过入口预算 |
| 单个异步 JavaScript chunk | 80 KiB | 防止任一功能页再次膨胀成大包 |
| JavaScript 总量 | 600 KiB | 防止过度拆分掩盖总体积增长 |
| 初始 CSS 合计 | 170 KiB | 约束首屏阻塞样式 |
| 单个异步 CSS chunk | 20 KiB | 约束页面专属样式 |
| CSS 总量 | 220 KiB | 防止样式总量无界增长 |

`npm run build --workspace @pal-control/console-web` 在 Vite 构建后自动读取 `.vite/manifest.json` 和实际产物大小。任一预算超限、manifest 缺少入口、静态依赖不存在或预算配置无效，命令都会以非零状态退出，因此 CI 中的常规前端构建就是发布门禁。检查不会采用 gzip 大小，避免压缩率变化掩盖真实下载与解析成本。

运行验证：

```powershell
npm run test --workspace @pal-control/console-web
npm run build --workspace @pal-control/console-web
```

修改预算必须随 PR 说明新增体积对应的用户价值，并优先考虑拆分大型页面、移除重复实现或将低频能力延后加载。不要仅为让构建通过而调高数字。
