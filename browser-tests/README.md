# CBS Support browser regression tests

These tests use Playwright Test against the real authenticated Razor pages. API and SignalR traffic is stubbed inside the browser so XSS, HTTP failure, timeout, empty/retry, and reconnect cases are deterministic without mutating a shared database.

Install and run:

```powershell
cd browser-tests
npm.cmd install
npx.cmd playwright install chromium
$env:CBS_SUPPORT_BROWSER_BASE_URL = 'https://localhost:5001'
$env:CBS_SUPPORT_BROWSER_CLIENT_STORAGE_STATE = 'C:\path\to\client-storage.json'
$env:CBS_SUPPORT_BROWSER_ADMIN_STORAGE_STATE = 'C:\path\to\admin-storage.json'
npm.cmd test
```

The two storage states must contain authenticated client and admin sessions respectively. CI should create those states using its test accounts and pass the base URL and state paths as protected job configuration. Without the base URL or role-specific state, the matching project is intentionally skipped rather than accidentally targeting a developer's local server.

Use `npm.cmd run test:headed` for local debugging or `npm.cmd run test:debug` for Playwright Inspector.
