import { expect, test } from '@playwright/test';
import {
  XSS_PAYLOAD,
  assertTextOnly,
  createApiState,
  installApiFixtures,
  installSignalRStub,
  openInterface
} from './fixtures.mjs';

test.skip(!process.env.CBS_SUPPORT_BROWSER_BASE_URL || !process.env.CBS_SUPPORT_BROWSER_ADMIN_STORAGE_STATE,
  'Set CBS_SUPPORT_BROWSER_BASE_URL and CBS_SUPPORT_BROWSER_ADMIN_STORAGE_STATE to run admin browser regression tests.');
test.beforeEach(async ({}, testInfo) => {
  testInfo.annotations.push({ type: 'environment', description: 'Requires CBS_SUPPORT_BROWSER_ADMIN_STORAGE_STATE for an authenticated admin session.' });
});

test('admin shell stays operable across supported viewport widths', async ({ page }) => {
  const state = createApiState();
  await installSignalRStub(page);
  await installApiFixtures(page, state);

  for (const viewport of [
    { width: 1440, height: 900 },
    { width: 1024, height: 800 },
    { width: 768, height: 1024 },
    { width: 390, height: 844 }
  ]) {
    await page.setViewportSize(viewport);
    await openInterface(page, '/AdminSupport');

    const layout = await page.evaluate(() => ({
      clientWidth: document.documentElement.clientWidth,
      scrollWidth: document.documentElement.scrollWidth,
      sidebarMaxHeight: getComputedStyle(document.querySelector('.admin-sidebar')).maxHeight
    }));
    expect(layout.scrollWidth).toBeLessThanOrEqual(layout.clientWidth);
    if (viewport.width <= 768) expect(layout.sidebarMaxHeight).toBe('none');

    await expect(page.getByRole('navigation', { name: 'Admin navigation' })).toBeVisible();
    await expect(page.getByRole('link', { name: 'Ticket Management' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Notifications' }).first()).toBeVisible();
  }
});

test('admin username, notification, ticket, and inquiry fields remain text-only', async ({ page }) => {
  const state = createApiState();
  await installSignalRStub(page);
  await installApiFixtures(page, state);
  await openInterface(page, '/AdminSupport');

  await expect(page.locator('#admin-username-display')).toHaveText(XSS_PAYLOAD);
  await expect(page.locator('#admin-username-display img, #admin-username-display script')).toHaveCount(0);
  await page.getByRole('button', { name: 'Notifications' }).click();
  await assertTextOnly(page.locator('#admin-notification-list .notification-message').first(), XSS_PAYLOAD);

  await page.getByRole('link', { name: 'Ticket Management' }).click();
  await expect(page.locator('#ticketsTable tbody')).toContainText(XSS_PAYLOAD);
  await page.getByRole('link', { name: 'Inquiry Management' }).click();
  await expect(page.locator('#inquiriesDataTable tbody')).toContainText(XSS_PAYLOAD);
  await expect(page.locator('#ticketsTable img, #ticketsTable script, #inquiriesDataTable img, #inquiriesDataTable script')).toHaveCount(0);
});

test('admin primary navigation is keyboard reachable and focused', async ({ page }) => {
  const state = createApiState();
  await installSignalRStub(page);
  await installApiFixtures(page, state);
  await openInterface(page, '/AdminSupport');

  for (const name of ['Dashboard', 'Chats', 'Ticket Management', 'Inquiry Management']) {
    await expect(page.getByRole('link', { name })).toBeVisible();
  }
  const chats = page.getByRole('link', { name: 'Chats' });
  await chats.focus();
  await expect.poll(() => page.evaluate(() => document.activeElement?.matches(':focus-visible'))).toBe(true);
  await page.keyboard.press('Enter');
  await expect(page.locator('#chats-page')).toHaveClass(/active/);
});

test('admin SignalR disconnect and reconnect are exposed in the chat status', async ({ page }) => {
  const state = createApiState();
  await installSignalRStub(page);
  await installApiFixtures(page, state);
  await openInterface(page, '/AdminSupport');
  await page.getByRole('link', { name: 'Chats' }).click();
  await page.evaluate(() => window.__testSignalR.emit('reconnecting', new Error('offline')));
  await expect(page.locator('#admin-chat-connection-status')).toContainText('Reconnecting');
  await page.evaluate(() => window.__testSignalR.emit('close', new Error('offline')));
  await expect(page.locator('#admin-chat-connection-status')).toContainText('Disconnected');
  await page.evaluate(() => window.__testSignalR.emit('reconnected', 'test-connection'));
  await expect(page.locator('#admin-chat-connection-status')).toContainText('Connected');
});

test('admin private-chat modal traps and restores focus', async ({ page }) => {
  const state = createApiState();
  await installSignalRStub(page);
  await installApiFixtures(page, state);
  await openInterface(page, '/AdminSupport');
  await page.getByRole('link', { name: 'Chats' }).click();

  const opener = page.getByRole('button', { name: /New private chat/i });
  await opener.focus();
  await opener.press('Enter');
  const modal = page.locator('#new-private-chat-modal');
  await expect(modal).toHaveClass(/show/);
  for (let i = 0; i < 8; i += 1) await page.keyboard.press('Tab');
  await expect.poll(() => modal.locator(':focus').count()).toBe(1);
  await page.keyboard.press('Escape');
  await expect(modal).not.toHaveClass(/show/);
  await expect(opener).toBeFocused();
});
