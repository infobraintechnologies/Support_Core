import { expect } from '@playwright/test';

export const XSS_PAYLOAD = '<img src=x onerror=globalThis.__xssTriggered=true><script>globalThis.__xssTriggered=true</script>';

export function createApiState() {
  return {
    messageStatus: 200,
    messageMode: 'success',
    loadingDelay: 500,
    messageItems: [{
      id: 9001,
      conversationId: 42,
      sequence: 1,
      text: XSS_PAYLOAD,
      sentAt: '2026-08-07T08:00:00Z',
      sender: { kind: 'Admin', userId: 7, displayName: XSS_PAYLOAD }
    }],
    writeStatus: 200,
    writeErrorText: XSS_PAYLOAD,
    writeDelay: 0,
    sendDelay: 0,
    notificationItems: [{
      id: 77,
      caseId: 42,
      eventType: 'TicketReplyCreated',
      title: XSS_PAYLOAD,
      message: XSS_PAYLOAD,
      createdAt: '2026-08-07T08:00:00Z',
      readAt: null
    }]
  };
}

export async function installSignalRStub(page) {
  await page.route('**/js/signalr/dist/browser/signalr.min.js**', route =>
    route.fulfill({ contentType: 'text/javascript', body: '// SignalR is controlled by the browser regression stub.' }));

  await page.addInitScript(() => {
    const connections = [];
    const makeConnection = () => {
      const handlers = new Map();
      const lifecycle = { close: [], reconnecting: [], reconnected: [] };
      const connection = {
        state: 0,
        on(name, handler) {
          if (!handlers.has(name)) handlers.set(name, []);
          handlers.get(name).push(handler);
        },
        onclose(handler) { lifecycle.close.push(handler); },
        onreconnecting(handler) { lifecycle.reconnecting.push(handler); },
        onreconnected(handler) { lifecycle.reconnected.push(handler); },
        async start() { this.state = 2; },
        async stop() { this.state = 0; lifecycle.close.forEach(handler => handler(new Error('test disconnect'))); },
        async invoke() { return undefined; },
        async send() { return undefined; },
        emit(name, payload) { (handlers.get(name) || []).forEach(handler => handler(payload)); },
        emitLifecycle(name, payload) { lifecycle[name].forEach(handler => handler(payload)); }
      };
      connections.push(connection);
      return connection;
    };

    globalThis.__testSignalR = {
      connections,
      emit(name, payload) {
        connections.forEach(connection => connection.emitLifecycle(name, payload));
      }
    };
    globalThis.signalR = {
      HubConnectionState: { Disconnected: 0, Connecting: 1, Connected: 2, Disconnecting: 3 },
      HubConnectionBuilder: class {
        withUrl() { return this; }
        withAutomaticReconnect() { return this; }
        build() { return makeConnection(); }
      }
    };
    if (!globalThis.Notification) {
      globalThis.Notification = { permission: 'denied', requestPermission: async () => 'denied' };
    }
  });
}

function json(route, body, status = 200) {
  return route.fulfill({
    status,
    contentType: 'application/json',
    body: JSON.stringify(body)
  });
}

function problem(status, detail) {
  return { status, title: `Test ${status}`, detail };
}

export async function installApiFixtures(page, state) {
  await page.route('**/*', async route => {
    const request = route.request();
    const url = new URL(request.url());
    const path = url.pathname;

    if (path === '/chathub' || path.startsWith('/chathub/')) return route.abort();
    if (path.endsWith('/js/signalr/dist/browser/signalr.min.js')) {
      return route.fulfill({ contentType: 'text/javascript', body: '// SignalR is controlled by the browser regression stub.' });
    }
    if (!path.startsWith('/api/') && !path.startsWith('/v1/api/')) return route.continue();

    if (path === '/v1/api/accounts/me') return json(route, { id: 7, name: XSS_PAYLOAD, role: 'Admin' });
    if (path === '/v1/api/clients') return json(route, [{ id: 1, name: 'Tenant One' }]);
    if (path.includes('/dashboard/stats')) {
      return json(route, {
        totalTickets: 0, totalInquiries: 0, resolvedTickets: 0,
        resolvedInquiries: 0, openTickets: 0, unsolvedInquiries: 0,
        criticalTickets: [], pendingInquiries: [], recentTickets: []
      });
    }
    if (path.includes('/notifications')) return json(route, { items: state.notificationItems, unreadCount: state.notificationItems.length });
    if (path.startsWith('/api/v1/admin/tickets')) {
      return json(route, { items: [{ id: 100, subject: XSS_PAYLOAD, createdBy: XSS_PAYLOAD, status: 'Open', priority: 'Normal', clientName: 'Tenant One' }] });
    }
    if (path.startsWith('/api/v1/admin/inquiries')) {
      return json(route, { items: [{ id: 101, topic: XSS_PAYLOAD, inquiredBy: XSS_PAYLOAD, outcome: 'Pending', clientName: 'Tenant One' }] });
    }
    if (path.endsWith('/instructions/tickets')) {
      return json(route, { data: [{ id: 100, subject: XSS_PAYLOAD, date: '2026-08-07T08:00:00Z', status: 'Open', priority: 'Normal', clientName: 'Tenant One' }] });
    }
    if (path.endsWith('/instructions/inquiries')) {
      return json(route, { data: [{ id: 101, topic: XSS_PAYLOAD, inquiredBy: XSS_PAYLOAD, date: '2026-08-07T08:00:00Z', outcome: 'Pending', clientName: 'Tenant One' }] });
    }
    if (request.method() === 'POST' && path.includes('/instructions/')) {
      if (state.writeDelay) await new Promise(resolve => setTimeout(resolve, state.writeDelay));
      if (state.writeStatus !== 200) return json(route, { message: state.writeErrorText }, state.writeStatus);
      return json(route, { id: 123 });
    }
    if (path === '/api/v1/conversations' || path === '/api/v1/conversations/') {
      return json(route, { items: [{ id: 42, kind: 'Group', state: 'Active', clientId: 1, version: 1, latestSequence: 1, unreadCount: 0 }], nextCursor: null });
    }
    if (/\/api\/v1\/conversations\/42\/messages$/.test(path)) {
      if (state.messageMode === 'timeout') return route.abort('timedout');
      if (request.method() === 'POST') {
        if (state.sendDelay) await new Promise(resolve => setTimeout(resolve, state.sendDelay));
        return json(route, {
          id: 9002,
          conversationId: 42,
          sequence: 2,
          text: 'duplicate test',
          sentAt: '2026-08-07T08:01:00Z',
          sender: { kind: 'Client', userId: 7, displayName: 'Test User' }
        });
      }
      if (state.messageMode === 'loading') await new Promise(resolve => setTimeout(resolve, state.loadingDelay));
      if (state.messageStatus !== 200) return json(route, problem(state.messageStatus, `Messages failed with ${state.messageStatus}.`), state.messageStatus);
      return json(route, { items: state.messageItems, nextCursor: null });
    }
    if (path.includes('/api/v1/conversations/42/read')) return json(route, {});
    if (path.includes('/api/v1/conversations/42')) return json(route, { id: 42, kind: 'Group', state: 'Active', clientId: 1, version: 1, latestSequence: 1 });
    if (path.includes('/api/v1/conversation-users')) return json(route, { items: [] });
    if (path.includes('/api/v1/conversations')) return json(route, { items: [], nextCursor: null });
    return json(route, {});
  });
}

export async function openInterface(page, path) {
  await page.goto(path, { waitUntil: 'domcontentloaded' });
  await expect(page.locator('body')).toBeVisible();
  if (new URL(page.url()).pathname.toLowerCase().startsWith('/login')) throw new Error(`The browser storage state is not authenticated for ${path}.`);
}

export async function assertTextOnly(locator, payload) {
  await expect(locator).toContainText(payload);
  await expect(locator.locator('img, script, iframe')).toHaveCount(0);
  const html = await locator.evaluate(element => element.innerHTML);
  expect(html).not.toContain(payload);
}
