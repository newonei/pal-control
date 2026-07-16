import AxeBuilder from "@axe-core/playwright";
import { expect, type Locator, type Page, test } from "@playwright/test";
import { mockPlayerApi } from "./playerApiMock";

async function openPortal(page: Page, options: { orderFailure?: boolean; closedZoneInRange?: boolean } = {}) {
  await mockPlayerApi(page, { authenticated: true, ...options });
  await page.goto("/");
  await expect(page.getByRole("heading", { level: 2, name: "战备商城", exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "购买", exact: true })).toBeEnabled();
  await expect(page.getByRole("img", { name: "新手补给图标" })).toBeVisible();
  await expect(page.getByText("优良", { exact: true })).toBeVisible();
  await expect(page.locator(".product-usage")).toContainText("用于新世界早期探索、捕捉与基础恢复。");
}

async function tabTo(page: Page, target: Locator, maximumTabs = 80) {
  for (let index = 0; index < maximumTabs; index += 1) {
    if (await target.evaluate((element) => element === document.activeElement)) return;
    await page.keyboard.press("Tab");
  }
  throw new Error(`Keyboard focus did not reach ${await target.getAttribute("aria-label") ?? "target"}.`);
}

async function expectNoSeriousAxeViolations(page: Page, context: string) {
  const result = await new AxeBuilder({ page })
    .withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"])
    .analyze();
  const severe = result.violations.filter((violation) =>
    violation.impact === "serious" || violation.impact === "critical"
  );
  expect(severe, `${context}: ${severe.map((item) => `${item.id}: ${item.help}`).join("; ")}`)
    .toEqual([]);
}

test("keyboard-only purchase dialog traps focus, restores it, and moves failed requests to the alert", async ({ page }) => {
  await openPortal(page, { orderFailure: true });

  await page.keyboard.press("Tab");
  const skipLink = page.getByRole("link", { name: "跳到主要内容" });
  await expect(skipLink).toBeFocused();
  await expect(skipLink).toBeVisible();
  await page.keyboard.press("Enter");
  await expect(page.locator("#main-content")).toBeFocused();

  const purchaseButton = page.getByRole("button", { name: "购买", exact: true });
  await tabTo(page, purchaseButton);
  await page.keyboard.press("Enter");

  const dialog = page.getByRole("dialog", { name: "确认购买" });
  const cancel = dialog.getByRole("button", { name: "取消" });
  const confirm = dialog.getByRole("button", { name: "确认并购买" });
  const decrease = dialog.getByRole("button", { name: "减少数量" });
  await expect(dialog).toBeVisible();
  await expect(cancel).toBeFocused();
  await expect(page.locator(".portal-background")).toHaveAttribute("inert", "");

  await page.keyboard.press("Tab");
  await expect(confirm).toBeFocused();
  await page.keyboard.press("Tab");
  await expect(decrease).toBeFocused();
  await page.keyboard.press("Shift+Tab");
  await expect(confirm).toBeFocused();

  await page.keyboard.press("Escape");
  await expect(dialog).toBeHidden();
  await expect(purchaseButton).toBeFocused();
  const outline = await purchaseButton.evaluate((element) => getComputedStyle(element).outlineStyle);
  expect(outline).not.toBe("none");

  await page.keyboard.press("Enter");
  await expect(cancel).toBeFocused();
  await page.keyboard.press("Tab");
  await page.keyboard.press("Enter");

  const alert = page.getByRole("alert").filter({ hasText: "发货队列暂时拥堵" });
  await expect(alert).toBeVisible();
  await expect(alert).toBeFocused();
  await expect(alert).toContainText("不要连续点击提交");
  await page.keyboard.press("Tab");
  await expect(alert.getByRole("button", { name: "关闭错误提示" })).toBeFocused();
  await page.keyboard.press("Enter");
  await expect(page.getByRole("heading", { level: 2, name: "战备商城", exact: true })).toBeFocused();
});

test.describe("mobile portal", () => {
  test.use({ viewport: { width: 375, height: 812 }, isMobile: true, hasTouch: true });

  test("keeps navigation, logout, content and dialog inside the viewport", async ({ page }, testInfo) => {
    await openPortal(page);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1)).toBe(true);

    const mobileNav = page.getByRole("navigation", { name: "玩家功能" });
    await expect(mobileNav).toBeVisible();
    await expect(mobileNav.getByRole("button", { name: "商城", exact: true })).toHaveAttribute("aria-current", "page");
    const logout = page.getByRole("button", { name: "安全退出" });
    await expect(logout).toBeVisible();
    const logoutBox = await logout.boundingBox();
    expect(logoutBox).not.toBeNull();
    expect(logoutBox!.height).toBeGreaterThanOrEqual(44);

    const boxes = await mobileNav.getByRole("button").evaluateAll((buttons) =>
      buttons.map((button) => ({ width: button.getBoundingClientRect().width, height: button.getBoundingClientRect().height }))
    );
    expect(boxes.every((box) => box.width >= 44 && box.height >= 44)).toBe(true);

    await mobileNav.getByRole("button", { name: "地图", exact: true }).tap();
    await expect(page.getByRole("group", { name: "地图图层" })).toBeVisible();
    await expect(page.getByRole("checkbox", { name: "我的位置" })).toBeChecked();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1)).toBe(true);

    await mobileNav.getByRole("button", { name: "订单", exact: true }).tap();
    await expect(page.getByRole("heading", { level: 2, name: "我的订单" })).toBeFocused();
    await expect(mobileNav.getByRole("button", { name: "订单", exact: true })).toHaveAttribute("aria-current", "page");

    await mobileNav.getByRole("button", { name: "结算", exact: true }).tap();
    await expect(page.getByRole("heading", { level: 2, name: "周档结算" })).toBeFocused();
    await expect(page.getByRole("heading", { name: "WEEK-E2E-01" })).toBeVisible();
    await expect(page.getByText("永久币已发放", { exact: true })).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1)).toBe(true);

    await mobileNav.getByRole("button", { name: "商城", exact: true }).tap();
    const purchase = page.getByRole("button", { name: "购买", exact: true });
    await purchase.tap();
    const dialog = page.getByRole("dialog", { name: "确认购买" });
    const box = await dialog.boundingBox();
    expect(box).not.toBeNull();
    expect(box!.x).toBeGreaterThanOrEqual(0);
    expect(box!.x + box!.width).toBeLessThanOrEqual(375);
    await expect(dialog.getByRole("button", { name: "取消" })).toBeFocused();
    await expect(page.locator(".portal-background")).toHaveAttribute("aria-hidden", "true");
    await expect(page.getByRole("link", { name: "跳到主要内容" })).toHaveCount(0);
    await page.screenshot({ path: testInfo.outputPath("mobile-portal.png") });
  });
});

test("map layers are keyboard-operable, display-only, and preserve authoritative text facts", async ({ page }) => {
  await openPortal(page, { closedZoneInRange: true });
  const zoneStep = page.locator(".onboarding-steps li").filter({ hasText: "进入资源兑换点" });
  await expect(zoneStep).not.toHaveClass(/complete/);
  await expect(zoneStep).toContainText("尚未完成");

  await page.getByRole("button", { name: "兑换点地图", exact: true }).click();
  await expect(page.getByRole("heading", { level: 2, name: "兑换点地图" })).toBeFocused();
  await expect(page.getByText("已在关闭兑换区范围内", { exact: true })).toBeVisible();
  await expect(page.getByText(/当前关闭，暂不可扫描或出售资源。下次开放：/)).toBeVisible();

  const selectedZone = page.locator(".selected-zone-name");
  await expect(selectedZone).toContainText("河湾开放兑换点");
  await expect(page.getByRole("img", { name: "从我的位置前往 河湾开放兑换点 的示意路线" })).toBeVisible();

  const layers = page.getByRole("group", { name: "地图图层" });
  await expect(layers).toBeVisible();
  const closedLayer = layers.getByRole("checkbox", { name: "关闭区" });
  await tabTo(page, closedLayer);
  await page.keyboard.press("Space");
  await expect(closedLayer).not.toBeChecked();
  await expect(page.getByRole("button", { name: "查看资源兑换点 火山关闭兑换点" })).toHaveCount(0);

  await page.locator(".zone-list button").filter({ hasText: "火山关闭兑换点" }).click();
  await expect(page.getByText("当前关闭 · 已进入有效范围", { exact: true })).toBeVisible();
  await expect(page.getByText(/该兑换点当前不可兑换，路线仅供预览/)).toBeVisible();
  await expect(page.getByRole("img", { name: "预览路线：从我的位置前往 火山关闭兑换点（当前不可兑换）" })).toBeVisible();
  await expect(page.getByText("预览路线（当前不可兑换）", { exact: true })).toBeVisible();
  await expect(page.getByText("严峻风险，兑换点关闭时不要等待在区域内。", { exact: true })).toBeVisible();
  await expect(page.getByText("资源潮汐", { exact: true })).toBeVisible();

  for (const name of ["热点", "风险", "前往路线", "我的位置"]) {
    await layers.getByRole("checkbox", { name }).uncheck();
  }
  await expect(page.locator(".extraction-hotspot-ring")).toHaveCount(0);
  await expect(page.locator(".extraction-risk-ring")).toHaveCount(0);
  await expect(page.locator(".extraction-player-route")).toHaveCount(0);
  await expect(page.locator(".extraction-player-marker")).toHaveCount(0);
  await expect(page.getByText("严峻风险，兑换点关闭时不要等待在区域内。", { exact: true })).toBeVisible();
  await expectNoSeriousAxeViolations(page, "map layer controls");
});

test("map facts survive unavailable own position and local basemap failure", async ({ page }) => {
  await mockPlayerApi(page, { authenticated: true, positionUnavailable: true });
  await page.goto("/");
  await page.getByRole("button", { name: "兑换点地图", exact: true }).click();
  await expect(page.getByText("当前位置暂不可用", { exact: true })).toBeVisible();
  await expect(page.getByText("沿河流向北，经过石桥后进入标记半径。", { exact: true })).toBeVisible();
  await expect(page.getByText("资源潮汐", { exact: true })).toBeVisible();

  await page.locator(".extraction-map-canvas > img").dispatchEvent("error");
  await expect(page.getByText("底图不可用，已显示校准网格", { exact: true })).toBeVisible();
  await expect(page.getByRole("group", { name: "地图图层" })).toBeVisible();
  await expect(page.getByText("沿河流向北，经过石桥后进入标记半径。", { exact: true })).toBeVisible();
  await expectNoSeriousAxeViolations(page, "map fallback without own position");
});

test("failed position polling revokes stale open-zone eligibility while preserving map facts", async ({ page }) => {
  const api = await mockPlayerApi(page, { authenticated: true, openZoneInRange: true });
  await page.goto("/");

  const zoneStep = page.locator(".onboarding-steps li").filter({ hasText: "进入资源兑换点" });
  await expect(zoneStep).toHaveClass(/complete/);
  api.setExtractionZonesUnavailable();

  await page.getByRole("button", { name: "兑换点地图", exact: true }).click();
  const status = page.locator(".range-status");
  await expect(status).toHaveClass(/unknown/);
  await expect(status).not.toHaveClass(/inside/);
  await expect(status.getByText("上次位置不可确认", { exact: true })).toBeVisible();
  await expect(status).toContainText("地图仍显示上次成功数据，但不可据此扫描或出售资源");
  await expect(page.getByText("河湾开放兑换点", { exact: true }).first()).toBeVisible();
  await expect(page.locator(".extraction-player-marker")).not.toHaveClass(/in-range/);
  await expectNoSeriousAxeViolations(page, "stale map after failed position polling");

  await page.getByRole("button", { name: "战备商城", exact: true }).click();
  await expect(zoneStep).not.toHaveClass(/complete/);
  await expect(zoneStep).toContainText("尚未完成");
});

test("documentation screenshots show map layers and resource presentation", async ({ page }, testInfo) => {
  await page.setViewportSize({ width: 1440, height: 1050 });
  await openPortal(page);
  await page.getByRole("button", { name: "兑换点地图", exact: true }).click();
  await expect(page.getByRole("group", { name: "地图图层" })).toBeVisible();
  await page.addStyleTag({ content: ".topbar { display: none !important; }" });
  await page.locator(".extraction-map-page").screenshot({
    path: testInfo.outputPath("resource-exchange-map-layers.png")
  });

  await page.getByRole("button", { name: "兑换记录", exact: true }).click();
  await page.getByRole("button", { name: "扫描可售资源" }).click();
  const dialog = page.getByRole("dialog", { name: "确认出售资源" });
  await expect(dialog.getByRole("img", { name: "骨头图标" })).toBeVisible();
  await dialog.screenshot({ path: testInfo.outputPath("resource-exchange-quote.png") });
});

test("login flow exposes named controls and focuses verification errors", async ({ page }) => {
  await mockPlayerApi(page, { authenticated: false });
  await page.goto("/");

  await expect(page.getByRole("main")).toBeVisible();
  await expect(page.getByRole("region", { name: "幻兽商域" })).toBeVisible();
  await expect(page.getByRole("region", { name: "玩家身份验证" })).toBeVisible();
  const userId = page.getByRole("textbox", { name: "平台 UserId / SteamID64" });
  await tabTo(page, userId, 10);
  await page.keyboard.type("steam_76561198000000000");
  await page.keyboard.press("Tab");
  await expect(page.getByRole("button", { name: "获取游戏内验证码" })).toBeFocused();
  await page.keyboard.press("Enter");

  const code = page.getByRole("textbox", { name: "8 位游戏内验证码" });
  await expect(code).toBeFocused();
  await page.keyboard.type("12345678");
  await page.keyboard.press("Tab");
  await expect(page.getByRole("button", { name: "进入玩家商城" })).toBeFocused();
  await page.keyboard.press("Enter");

  const alert = page.getByRole("alert");
  await expect(alert).toContainText("验证码不正确");
  await expect(alert).toBeFocused();
  await expect(code).toHaveAttribute("aria-invalid", "true");
  await expect(code).toHaveAttribute("aria-describedby", /verify-code-expiry login-status/);
});

test("login, portal and modal have no serious or critical axe violations", async ({ page }) => {
  await mockPlayerApi(page, { authenticated: false });
  await page.goto("/");
  await expect(page.getByRole("heading", { name: "玩家身份验证" })).toBeVisible();
  await expectNoSeriousAxeViolations(page, "login");

  await page.unroute("**/api/v1/player/**");
  await mockPlayerApi(page, { authenticated: true });
  await page.reload();
  await expect(page.getByRole("button", { name: "购买", exact: true })).toBeEnabled();
  await expectNoSeriousAxeViolations(page, "portal");

  await page.getByRole("button", { name: "周档结算", exact: true }).click();
  await expect(page.getByRole("heading", { name: "WEEK-E2E-01" })).toBeVisible();
  await expectNoSeriousAxeViolations(page, "weekly settlement");

  await page.getByRole("button", { name: /消息中心/ }).click();
  await expect(page.getByRole("heading", { level: 2, name: "消息中心" })).toBeVisible();
  await expect(page.getByText("游戏内提醒能力不可用，已保留站内消息")).toBeVisible();
  await expectNoSeriousAxeViolations(page, "notification center");

  await page.getByRole("button", { name: "战备商城", exact: true }).click();

  await page.getByRole("button", { name: "购买", exact: true }).click();
  await expect(page.getByRole("dialog", { name: "确认购买" })).toBeVisible();
  await expectNoSeriousAxeViolations(page, "purchase dialog");
});

test("self-only notification center marks reads and enables browser notifications only after a click", async ({ page }) => {
  // Headless Chromium on GitHub-hosted Windows can report `denied` even after
  // BrowserContext.grantPermissions. Use a deterministic in-page Notification
  // contract so this test verifies our explicit-click behavior instead of the
  // runner's desktop notification policy.
  await page.addInitScript(() => {
    class TestNotification {
      static permission: NotificationPermission = "default";

      static async requestPermission(): Promise<NotificationPermission> {
        const count = Number(sessionStorage.getItem("e2e-notification-request-count") ?? "0");
        sessionStorage.setItem("e2e-notification-request-count", String(count + 1));
        TestNotification.permission = "granted";
        return TestNotification.permission;
      }

      constructor(_title: string, _options?: NotificationOptions) {}
    }

    Object.defineProperty(window, "Notification", {
      configurable: true,
      value: TestNotification
    });
  });
  await openPortal(page);

  const notificationTab = page.getByRole("button", { name: /消息中心/ });
  await expect(notificationTab).toContainText("1");
  await notificationTab.click();
  await expect(page.getByRole("heading", { level: 3, name: "站内消息与投递状态" })).toBeVisible();
  await expect(page.getByText("资源兑换结果待核对", { exact: true })).toBeVisible();
  await expect(page.getByText("请勿重复购买或重复结算，等待管理员核对。", { exact: true })).toBeVisible();
  await expect(page.getByText("游戏内提醒能力不可用，已保留站内消息", { exact: true })).toBeVisible();

  await expect(page.evaluate(() => Notification.permission)).resolves.toBe("default");
  await expect(page.evaluate(() => sessionStorage.getItem("e2e-notification-request-count"))).resolves.toBeNull();
  await expect(page.evaluate(() => localStorage.getItem("pal-player-browser-notifications-v1"))).resolves.toBeNull();
  await page.getByRole("button", { name: "启用浏览器提醒" }).click();
  await expect(page.getByText("浏览器提醒已启用", { exact: true })).toBeVisible();
  await expect(page.evaluate(() => Notification.permission)).resolves.toBe("granted");
  await expect(page.evaluate(() => sessionStorage.getItem("e2e-notification-request-count"))).resolves.toBe("1");
  await expect(page.evaluate(() => localStorage.getItem("pal-player-browser-notifications-v1"))).resolves.toBe("enabled");

  await page.getByRole("button", { name: "标为已读", exact: true }).click();
  await expect(page.getByText("消息均已读", { exact: true })).toBeVisible();
  await expect(notificationTab.getByLabel("1 条未读消息")).toHaveCount(0);
});

test("read-only federation panel isolates node failures and never forwards identity in server links", async ({ page }, testInfo) => {
  await openPortal(page);
  await page.getByRole("button", { name: "我的服务器", exact: true }).click();
  await expect(page.getByRole("heading", { level: 2, name: "我的服务器" })).toBeFocused();

  const panel = page.getByRole("region", { name: "我的服务器" });
  await expect(panel.locator(".federation-server-card")).toHaveCount(3);
  await expect(panel.getByText("晨曦周世界", { exact: true })).toBeVisible();
  await expect(panel.getByText("1,240 商域币", { exact: true })).toBeVisible();
  await expect(panel.getByText("远山周世界", { exact: true })).toBeVisible();
  await expect(panel.getByText("860 商域币", { exact: true })).toBeVisible();

  const quarantined = panel.locator(".federation-server-card").filter({ hasText: "群岛周世界" });
  await expect(quarantined.getByText("版本不兼容", { exact: true })).toBeVisible();
  await expect(quarantined.getByText("余额不可用", { exact: true })).toBeVisible();
  await expect(quarantined.locator(".federation-balance")).not.toContainText(/^0$/);

  const switchLink = panel.getByRole("link", { name: "打开该服玩家门户" });
  await expect(switchLink).toHaveCount(1);
  await expect(switchLink).toHaveAttribute("href", "https://beta.demo.invalid/player/");
  await expect(switchLink).toHaveAttribute("rel", "noopener noreferrer");
  await expect(switchLink).toHaveAttribute("referrerpolicy", "no-referrer");
  expect(new URL((await switchLink.getAttribute("href"))!).search).toBe("");

  const markup = await panel.evaluate(element => element.outerHTML);
  expect(markup).not.toMatch(/subjectToken|signingKey|identityKey|signature|accountId|playerUid|steamId|userId|fed2_/i);
  await expectNoSeriousAxeViolations(page, "federation server registry");
  await panel.screenshot({ path: testInfo.outputPath("server-federation.png") });
});

test("selective resource sale survives a lost response and refresh without consuming unselected items", async ({ page }) => {
  const mock = await mockPlayerApi(page, {
    authenticated: true,
    selectionResponseLoss: true
  });
  await page.goto("/");
  await expect(page.getByRole("heading", { level: 2, name: "战备商城" })).toBeVisible();
  await page.getByRole("button", { name: "兑换记录", exact: true }).click();
  await page.getByRole("button", { name: "扫描可售资源" }).click();

  const dialog = page.getByRole("dialog", { name: "确认出售资源" });
  await expect(dialog).toBeVisible();
  await expect(dialog.getByRole("img", { name: "皮革图标" })).toBeVisible();
  await expect(dialog.getByRole("img", { name: "骨头图标" })).toBeVisible();
  await expect(dialog.getByText("稀有", { exact: true })).toBeVisible();
  await expect(dialog.getByText("用途：用于古代科技与稀有制造。", { exact: true })).toBeVisible();
  await expect(dialog.getByRole("checkbox", { name: "选择 皮革" })).toBeChecked();
  await dialog.getByRole("checkbox", { name: "选择 皮革" }).uncheck();
  await dialog.getByRole("spinbutton", { name: "骨头 出售数量" }).fill("2");
  await expect(dialog.getByText("已选 1 项 / 2 件", { exact: true })).toBeVisible();
  await expect(dialog.getByRole("status").getByText("券 6", { exact: true })).toBeVisible();
  await expectNoSeriousAxeViolations(page, "selective resource review draft");

  await dialog.getByRole("button", { name: "复核所选资源" }).click();
  await expect(dialog.getByText("本次只扣除上方已选的 2 件资源，未选资源保留在背包。")).toBeVisible();
  await dialog.getByRole("button", { name: "确认出售所选资源" }).click();
  await expect(dialog.getByRole("alert")).toBeVisible();
  expect(mock.selectionRequests).toHaveLength(1);
  expect(mock.selectionRequests[0].body.items).toEqual([{ itemId: "Bone", quantity: 2 }]);
  expect(mock.selectionRequests[0].body.items.some((item) => item.itemId === "Leather")).toBe(false);
  const firstKey = mock.selectionRequests[0].idempotencyKey;
  await expect(page.evaluate(() => sessionStorage.getItem("pal-control-selective-sale-v1")))
    .resolves.toContain(firstKey);

  await page.reload();
  const restored = page.getByRole("dialog", { name: "确认出售资源" });
  await expect(restored).toBeVisible();
  await expect(restored.getByRole("checkbox", { name: "选择 皮革" })).not.toBeChecked();
  await expect(restored.getByRole("spinbutton", { name: "骨头 出售数量" })).toHaveValue("2");
  await restored.getByRole("button", { name: "复核所选资源" }).click();
  await restored.getByRole("button", { name: "确认出售所选资源" }).click();

  await expect(page.getByRole("status").filter({ hasText: "未选资源未进入扣物清单" })).toBeVisible();
  expect(mock.selectionRequests).toHaveLength(2);
  expect(mock.selectionRequests[1]).toEqual(mock.selectionRequests[0]);
  expect(mock.settlementRequests).toHaveLength(1);
  expect(mock.settlementRequests[0].runId).toBe("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
  await expect(page.evaluate(() => sessionStorage.getItem("pal-control-selective-sale-v1")))
    .resolves.toBeNull();
});
