import AxeBuilder from "@axe-core/playwright";
import { expect, test } from "@playwright/test";
import { mockPlayerApi } from "./playerApiMock";

const rawPlayerIdentity = "steam_76561198000000000";
const rawTeamId = "a1000000-0000-0000-0000-000000000001";
const invitationToken =
  "tm1.a2000000000000000000000000000002.safe-e2e-invite-material-0123456789abcd";

async function openTeam(page: import("@playwright/test").Page, mode: "none" | "owner" | "member" = "owner") {
  const mock = await mockPlayerApi(page, { authenticated: true, teamMode: mode });
  await page.goto("/");
  await page.getByRole("button", { name: /团队/ }).click();
  return mock;
}

async function expectNoSeriousAxeViolations(page: import("@playwright/test").Page) {
  const result = await new AxeBuilder({ page })
    .withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"])
    .analyze();
  expect(result.violations.filter((item) =>
    item.impact === "serious" || item.impact === "critical"
  )).toEqual([]);
}

test("owner sees four authoritative goals, private contributions, and three team boards", async ({ page }, testInfo) => {
  await openTeam(page);

  await expect(page.getByRole("heading", { level: 2, name: "团队协作" })).toBeFocused();
  await expect(page.getByRole("heading", { name: "苍穹搬运社" })).toBeVisible();
  await expect(page.getByRole("heading", { name: "本周合作目标" })).toBeVisible();
  await expect(page.getByRole("progressbar")).toHaveCount(4);
  await expect(page.getByText("团队累计", { exact: true })).toBeVisible();
  await expect(page.getByText("我的贡献", { exact: true })).toBeVisible();
  await expect(page.getByRole("heading", { name: "资源价值榜" })).toBeVisible();
  await expect(page.getByRole("heading", { name: "可靠任务积分榜" })).toBeVisible();
  await expect(page.getByRole("heading", { name: "成功送达榜" })).toBeVisible();
  await expect(page.getByText("权威投影正常", { exact: true })).toBeVisible();
  await expect(page.locator("body")).not.toContainText(rawPlayerIdentity);
  await expect(page.locator("body")).not.toContainText(rawTeamId);
  await expectNoSeriousAxeViolations(page);
  await page.addStyleTag({ content: ".topbar { display: none !important; }" });
  await page.locator(".team-economy").screenshot({ path: testInfo.outputPath("team-economy.png") });
});

test("invitation is sent with CSRF/idempotency and disappears on reload without browser persistence", async ({ page }) => {
  const mock = await openTeam(page);
  await page.getByRole("button", { name: "生成并轮换邀请" }).click();

  await expect(page.getByText(invitationToken, { exact: true })).toBeVisible();
  expect(mock.teamRequests).toHaveLength(1);
  expect(mock.teamRequests[0]).toMatchObject({
    path: "/me/team-economy/invite/rotate",
    method: "POST",
    csrfToken: "csrf-e2e",
    body: { maximumUses: 10 }
  });
  expect(mock.teamRequests[0].idempotencyKey).toMatch(/^[0-9a-f-]{36}$/i);
  await expect(page.evaluate((token) => ({
    local: Object.values(localStorage).includes(token),
    session: Object.values(sessionStorage).includes(token)
  }), invitationToken)).resolves.toEqual({ local: false, session: false });

  await page.reload();
  await page.getByRole("button", { name: /团队/ }).click();
  await expect(page.getByText(invitationToken, { exact: true })).toHaveCount(0);
});

test("no-team join uses a password field and never accepts an identity override", async ({ page }) => {
  const mock = await openTeam(page, "none");
  const tokenField = page.getByLabel("一次性邀请 token");
  await expect(tokenField).toHaveAttribute("type", "password");
  await tokenField.fill(invitationToken);
  await page.getByRole("button", { name: "加入", exact: true }).click();

  await expect(page.getByRole("heading", { name: "苍穹搬运社" })).toBeVisible();
  expect(mock.teamRequests).toHaveLength(1);
  expect(mock.teamRequests[0].body).toEqual({ token: invitationToken });
  for (const forbidden of ["accountId", "userId", "playerUid", "steamId", "serverId", "seasonId", "teamId"]) {
    expect(mock.teamRequests[0].body).not.toHaveProperty(forbidden);
  }
});

test.describe("mobile team economy", () => {
  test.use({ viewport: { width: 375, height: 812 }, isMobile: true, hasTouch: true });

  test("keeps goals, boards, and owner controls inside the viewport", async ({ page }) => {
    await openTeam(page);
    await expect(page.getByRole("heading", { name: "本周合作目标" })).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1))
      .toBe(true);
    const controls = await page.getByRole("button").evaluateAll((buttons) => buttons
      .filter((button) => {
        const box = button.getBoundingClientRect();
        return box.width > 0 && box.height > 0;
      })
      .map((button) => ({
        label: button.getAttribute("aria-label") ?? button.textContent?.trim() ?? "button",
        width: button.getBoundingClientRect().width,
        height: button.getBoundingClientRect().height
      })));
    expect(controls.filter((box) => box.width < 44 || box.height < 44)).toEqual([]);
    await expectNoSeriousAxeViolations(page);
  });
});
