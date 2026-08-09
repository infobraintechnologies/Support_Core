import { expect, test } from '@playwright/test';
import {
  XSS_PAYLOAD,
  assertTextOnly,
  createApiState,
  installApiFixtures,
  installSignalRStub,
  openInterface
} from './fixtures.mjs';

test.skip(!process.env.CBS_SUPPORT_BROWSER_BASE_URL || !process.env.CBS_SUPPORT_BROWSER_CLIENT_STORAGE_STATE,
  'Set CBS_SUPPORT_BROWSER_BASE_URL and CBS_SUPPORT_BROWSER_CLIENT_STORAGE_STATE to run support browser regression tests.');
test.beforeEach(async ({}, testInfo) => {
  testInfo.annotations.push({ type: 'environment', description: 'Requires CBS_SUPPORT_BROWSER_CLIENT_STORAGE_STATE for an authenticated client session.' });
});

test('client workspace stays operable across supported viewport widths', async ({ page }) => {
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
    await openInterface(page, '/Support');

    const layout = await page.evaluate(() => ({
      clientWidth: document.documentElement.clientWidth,
      scrollWidth: document.documentElement.scrollWidth,
      bodyOverflowY: getComputedStyle(document.body).overflowY
    }));
    expect(layout.scrollWidth).toBeLessThanOrEqual(layout.clientWidth);
    if (viewport.width <= 768) expect(layout.bodyOverflowY).not.toBe('hidden');

    await expect(page.getByRole('button', { name: 'Notifications' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'New Support Ticket' })).toBeVisible();
    await expect(page.getByLabel('Search conversations')).toBeVisible();
  }
});

test('client notification and toast payloads are rendered as text', async ({ page }) => {
  const state = createApiState();
  await installSignalRStub(page);
  await installApiFixtures(page, state);
  await openInterface(page, '/Support');

  await page.getByRole('button', { name: 'Notifications' }).click();
  await assertTextOnly(page.locator('#client-notification-list .notification-message').first(), XSS_PAYLOAD);

  state.writeStatus = 400;
  await page.getByRole('button', { name: 'New Support Ticket' }).click();
  await page.getByLabel(/Full Name/).fill('Test User');
  await page.getByLabel('Subject *').selectOption('ticket/training');
  await page.getByLabel(/Description/).fill('Test description');
  await page.getByLabel(/Expected Resolution Time/).fill('2026-08-08T10:00');
  await page.getByRole('button', { name: 'Submit Request' }).click();
  await assertTextOnly(page.locator('.toast-body').last(), `Error: ${XSS_PAYLOAD}`);
});

test('stored messages, tickets, and inquiries stay text-only', async ({ page }) => {
  const state = createApiState();
  await installSignalRStub(page);
  await installApiFixtures(page, state);

  await openInterface(page, '/Support');
  await page.getByRole('button', { name: /Tenant One group/ }).click();
  await assertTextOnly(page.locator('#chat-panel-body .message-text').first(), XSS_PAYLOAD);
  await expect(page.locator('#chat-panel-body img, #chat-panel-body script')).toHaveCount(0);
  await expect(page.locator('#supportTicketsDataTable tbody')).toContainText(XSS_PAYLOAD);
  await expect(page.locator('#inquiriesDataTable tbody')).toContainText(XSS_PAYLOAD);
  await expect(page.locator('#supportTicketsDataTable img, #supportTicketsDataTable script, #inquiriesDataTable img, #inquiriesDataTable script')).toHaveCount(0);

});

test('primary support controls are keyboard reachable with visible focus and named controls', async ({ page }) => {
  const state = createApiState();
  await installSignalRStub(page);
  await installApiFixtures(page, state);

  await openInterface(page, '/Support');
  await expect(page.getByRole('button', { name: 'Toggle fullscreen' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Notifications' })).toBeVisible();
  await expect(page.getByLabel('Search conversations')).toBeVisible();
  await expect(page.getByLabel('Message')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Send message' })).toBeVisible();
  await page.getByRole('button', { name: 'New Support Ticket' }).focus();
  await page.keyboard.press('Enter');
  await expect(page.locator('#newSupportTicketModal')).toHaveClass(/show/);
  await expect.poll(() => page.evaluate(() => document.activeElement?.matches(':focus-visible'))).toBe(true);

});

test('modal focus is trapped and restored to the opener', async ({ page }) => {
  const state = createApiState();
  await installSignalRStub(page);
  await installApiFixtures(page, state);
  await openInterface(page, '/Support');

  const opener = page.getByRole('button', { name: 'New Support Ticket' });
  await opener.focus();
  await opener.press('Enter');
  const modal = page.locator('#newSupportTicketModal');
  await expect(modal).toHaveClass(/show/);
  await expect.poll(() => modal.locator(':focus').count()).toBe(1);
  for (let i = 0; i < 12; i += 1) await page.keyboard.press('Tab');
  await expect.poll(() => modal.locator(':focus').count()).toBe(1);
  await page.keyboard.press('Escape');
  await expect(modal).not.toHaveClass(/show/);
  await expect(opener).toBeFocused();
});

test('multiple support rows keep IDs unique, modal content isolated, and focus restorable', async ({ page }) => {
  const state = createApiState();
  await installSignalRStub(page);
  await installApiFixtures(page, state);
  await openInterface(page, '/Support');

  await expect(page.locator('#supportTicketsDataTable tbody tr')).toHaveCount(2);
  await expect(page.locator('#inquiriesDataTable tbody tr')).toHaveCount(2);

  const accessibilityIssues = await page.evaluate(() => {
    const elements = [...document.querySelectorAll('[id]')];
    const counts = new Map();
    for (const element of elements) counts.set(element.id, (counts.get(element.id) || 0) + 1);

    const duplicateIds = [...counts.entries()]
      .filter(([, count]) => count > 1)
      .map(([id]) => id);
    const missingAriaReferences = [];
    for (const element of document.querySelectorAll('[aria-labelledby], [aria-describedby]')) {
      for (const attribute of ['aria-labelledby', 'aria-describedby']) {
        for (const id of (element.getAttribute(attribute) || '').split(/\s+/).filter(Boolean)) {
          if (!document.getElementById(id)) missingAriaReferences.push(`${element.id}:${attribute}:${id}`);
        }
      }
    }
    const missingLabelTargets = [...document.querySelectorAll('label[for]')]
      .filter(label => !document.getElementById(label.htmlFor))
      .map(label => `${label.htmlFor}:${label.textContent.trim()}`);

    return { duplicateIds, missingAriaReferences, missingLabelTargets };
  });
  expect(accessibilityIssues).toEqual({
    duplicateIds: [],
    missingAriaReferences: [],
    missingLabelTargets: []
  });

  const ticketModal = page.locator('#viewTicketDetailsModal');
  const firstTicketOpener = page.locator('#view-ticket-details-100');
  await firstTicketOpener.focus();
  await page.keyboard.press('Enter');
  await expect(ticketModal).toHaveClass(/show/);
  await expect(ticketModal.locator('#details-id')).toHaveText('#100');
  await expect(ticketModal.locator('#details-subject')).toHaveText('Migration issue');
  await expect(ticketModal.locator('#details-description')).toHaveText('Migration details for ticket 100.');
  await expect(page.locator('.modal.show')).toHaveCount(1);
  await expect.poll(() => page.evaluate(() => document.activeElement?.closest('#viewTicketDetailsModal') !== null)).toBe(true);
  for (let i = 0; i < 10; i += 1) await page.keyboard.press('Tab');
  await expect.poll(() => page.evaluate(() => document.activeElement?.closest('#viewTicketDetailsModal') !== null)).toBe(true);
  await page.keyboard.press('Escape');
  await expect(ticketModal).not.toHaveClass(/show/);
  await expect(firstTicketOpener).toBeFocused();

  const secondTicketOpener = page.locator('#view-ticket-details-99');
  await secondTicketOpener.click();
  await expect(ticketModal.locator('#details-id')).toHaveText('#99');
  await expect(ticketModal.locator('#details-subject')).toContainText('script');
  await expect(ticketModal.locator('#details-description')).toHaveText('Training details for ticket 99.');
  await ticketModal.getByRole('button', { name: 'Close' }).click();
  await expect(secondTicketOpener).toBeFocused();

  const inquiryModal = page.locator('#viewInquiryDetailsModal');
  await page.locator('#view-inquiry-details-101').click();
  await expect(inquiryModal).toHaveClass(/show/);
  await expect(inquiryModal.locator('#inquiry-details-id')).toHaveText('#INQ-101');
  await expect(inquiryModal.locator('#inquiry-details-topic')).toHaveText('Account access');
  await expect(inquiryModal.locator('#inquiry-details-description')).toHaveText('Access details for inquiry 101.');
  await inquiryModal.getByRole('button', { name: 'Close' }).click();
  await expect(page.locator('#view-inquiry-details-101')).toBeFocused();
});

for (const status of [400, 401, 403, 409, 429, 500]) {
  test(`support message failure ${status} has a retryable UI state`, async ({ page }) => {
    const state = createApiState();
    state.messageStatus = status;
    await installSignalRStub(page);
    await installApiFixtures(page, state);
    await openInterface(page, '/Support');

    const response = page.waitForResponse(item =>
      item.url().includes('/api/v1/conversations/42/messages') && item.status() === status);
    await page.getByRole('button', { name: /Tenant One group/ }).click();
    await response;
    await expect(page.locator('#chat-panel-body')).toContainText('Messages could not be loaded.');
    await expect(page.getByRole('button', { name: 'Retry' })).toBeVisible();
  });
}

test('loading, empty, network timeout, and retry states are observable', async ({ page }) => {
  const state = createApiState();
  state.messageMode = 'loading';
  await installSignalRStub(page);
  await installApiFixtures(page, state);
  await openInterface(page, '/Support');
  await page.getByRole('button', { name: /Tenant One group/ }).click();
  await expect(page.locator('#chat-panel-body')).toContainText('Loading messages');

  state.messageMode = 'success';
  await expect(page.locator('#chat-panel-body .message-text').first()).toBeVisible();

  state.messageMode = 'timeout';
  await page.reload({ waitUntil: 'domcontentloaded' });
  await page.getByRole('button', { name: /Tenant One group/ }).click();
  await expect(page.locator('#chat-panel-body')).toContainText('Messages could not be loaded.');

  state.messageMode = 'success';
  state.messageItems = [];
  await page.getByRole('button', { name: 'Retry' }).click();
  await expect(page.locator('#chat-panel-body')).toContainText('No messages yet. Say hello to start the conversation.');
});

test('SignalR disconnect and reconnect are reflected in the support UI', async ({ page }) => {
  const state = createApiState();
  await installSignalRStub(page);
  await installApiFixtures(page, state);
  await openInterface(page, '/Support');
  await page.evaluate(() => window.__testSignalR.emit('reconnecting', new Error('offline')));
  await expect(page.locator('#connection-status')).toHaveText('Reconnecting…');
  await page.evaluate(() => window.__testSignalR.emit('close', new Error('offline')));
  await expect(page.locator('#connection-status')).toHaveText('Disconnected');
  await page.evaluate(() => window.__testSignalR.emit('reconnected', 'test-connection'));
  await expect(page.locator('#connection-status')).toHaveText('Connected');
});

test('duplicate ticket submission is prevented while the request is in flight', async ({ page }) => {
  const state = createApiState();
  state.writeDelay = 500;
  await installSignalRStub(page);
  await installApiFixtures(page, state);
  await openInterface(page, '/Support');
  await page.getByRole('button', { name: 'New Support Ticket' }).click();
  await page.getByLabel(/Full Name/).fill('Test User');
  await page.getByLabel('Subject *').selectOption('ticket/training');
  await page.getByLabel(/Description/).fill('Test description');
  await page.getByLabel(/Expected Resolution Time/).fill('2026-08-08T10:00');

  const requests = [];
  page.on('request', request => {
    if (request.method() === 'POST' && request.url().includes('/v1/api/instructions/ticket/')) requests.push(request);
  });
  const submit = page.getByRole('button', { name: 'Submit Request' });
  const response = page.waitForResponse(item =>
    item.url().includes('/v1/api/instructions/ticket/') && item.request().method() === 'POST');
  await page.evaluate(() => {
    const form = document.getElementById('supportTicketForm');
    form.requestSubmit();
    form.requestSubmit();
  });
  await expect(submit).toHaveAttribute('aria-busy', 'true');
  await response;
  expect(requests).toHaveLength(1);
});

test('duplicate message submission is prevented while the request is in flight', async ({ page }) => {
  const state = createApiState();
  state.sendDelay = 500;
  await installSignalRStub(page);
  await installApiFixtures(page, state);
  await openInterface(page, '/Support');
  await page.getByRole('button', { name: /Tenant One group/ }).click();
  const input = page.getByLabel('Message');
  await input.fill('duplicate test');

  const requests = [];
  page.on('request', request => {
    if (request.method() === 'POST' && request.url().includes('/api/v1/conversations/42/messages')) requests.push(request);
  });
  const response = page.waitForResponse(item =>
    item.url().includes('/api/v1/conversations/42/messages') && item.request().method() === 'POST');
  await page.evaluate(() => {
    const messageInput = document.getElementById('message-input');
    const event = new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true });
    messageInput.dispatchEvent(event);
    messageInput.dispatchEvent(event);
  });
  await response;
  expect(requests).toHaveLength(1);
});
