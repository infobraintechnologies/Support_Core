import { expect, test } from '@playwright/test';

test.skip(!process.env.CBS_SUPPORT_BROWSER_BASE_URL,
  'Set CBS_SUPPORT_BROWSER_BASE_URL to run login browser tests.');

async function openLogin(page) {
  await page.goto('/Login', { waitUntil: 'domcontentloaded' });
  await expect(page.getByRole('heading', { name: 'Sign in', exact: true })).toBeVisible();
}

for (const viewport of [
  { width: 1440, height: 900 },
  { width: 1024, height: 800 },
  { width: 768, height: 1024 },
  { width: 390, height: 844 }
]) {
  test(`login remains usable at ${viewport.width}px`, async ({ page }) => {
    await page.setViewportSize(viewport);
    await openLogin(page);

    const layout = await page.evaluate(() => ({
      clientWidth: document.documentElement.clientWidth,
      scrollWidth: document.documentElement.scrollWidth
    }));
    expect(layout.scrollWidth).toBeLessThanOrEqual(layout.clientWidth);

    await expect(page.getByRole('group', { name: 'Sign in as' })).toBeVisible();
    await expect(page.getByLabel('Username', { exact: true }).first()).toBeVisible();
    await expect(page.getByLabel('Password', { exact: true }).first()).toBeVisible();
    await expect(page.getByRole('button', { name: 'Sign in' })).toBeVisible();

    const targetSizes = await page.evaluate(() => ({
      rememberRow: document.querySelector('.form-check')?.getBoundingClientRect().height ?? 0,
      submit: document.querySelector('.btn-login')?.getBoundingClientRect().height ?? 0
    }));
    expect(targetSizes.rememberRow).toBeGreaterThanOrEqual(40);
    expect(targetSizes.submit).toBeGreaterThanOrEqual(40);
  });
}

test('login role choices are labeled native radios with visible keyboard focus', async ({ page }) => {
  await openLogin(page);

  const roleGroup = page.getByRole('group', { name: 'Sign in as' });
  const admin = page.getByRole('radio', { name: 'Admin' });
  const client = page.getByRole('radio', { name: 'Client' });

  await expect(roleGroup).toBeVisible();
  await expect(admin).toBeVisible();
  await expect(client).toBeVisible();
  await expect(admin).toBeChecked();
  await expect(admin).toHaveAttribute('type', 'radio');
  await expect(client).toHaveAttribute('type', 'radio');
  await expect(page.locator('#adminRole + label')).toHaveAttribute('for', 'adminRole');
  await expect(page.locator('#clientRole + label')).toHaveAttribute('for', 'clientRole');

  const radioStyles = await page.locator('#adminRole').evaluate(element => {
    const style = getComputedStyle(element);
    return { display: style.display, visibility: style.visibility };
  });
  expect(radioStyles).toEqual({ display: 'block', visibility: 'visible' });

  await admin.focus();
  await expect(admin).toBeFocused();
  await expect.poll(() => page.evaluate(() => document.activeElement?.matches(':focus-visible'))).toBe(true);
  await page.keyboard.press('ArrowRight');
  await expect(client).toBeChecked();
  await expect(client).toBeFocused();
  await expect(page.locator('#clientForm')).toBeVisible();
  await expect(page.locator('#clientForm input:not([type="hidden"]):enabled')).toHaveCount(3);
  await page.keyboard.press('ArrowLeft');
  await expect(admin).toBeChecked();
  await expect(admin).toBeFocused();
  await expect(page.locator('#adminForm')).toBeVisible();
});

test('login fields have labels and inactive role fields leave the tab order', async ({ page }) => {
  await openLogin(page);

  const inputLabels = await page.locator('input:not([type="hidden"])').evaluateAll(inputs =>
    inputs.map(input => ({
      id: input.id,
      disabled: input.disabled,
      labelFor: document.querySelector(`label[for="${CSS.escape(input.id)}"]`)?.getAttribute('for') ?? null
    })));
  expect(inputLabels.every(input => input.id && input.labelFor === input.id)).toBe(true);

  await expect(page.locator('#clientForm')).toHaveAttribute('hidden', '');
  await expect(page.locator('#clientForm input')).toHaveCount(4);
  await expect(page.locator('#clientForm input:enabled')).toHaveCount(0);

  const admin = page.getByRole('radio', { name: 'Admin' });
  const username = page.getByLabel('Username', { exact: true }).first();
  await admin.focus();
  await page.keyboard.press('Tab');
  await expect(username).toBeFocused();

  await page.keyboard.press('Shift+Tab');
  await expect(admin).toBeFocused();
  await page.keyboard.press('ArrowRight');
  await expect(page.getByRole('radio', { name: 'Client' })).toBeChecked();
  await expect(page.locator('#adminForm')).toHaveAttribute('hidden', '');
  await expect(page.locator('#adminForm input:enabled')).toHaveCount(0);
});

test('password recovery is not presented as an active reset link without a recovery flow', async ({ page }) => {
  await openLogin(page);

  await expect(page.getByRole('link', { name: /forgot password/i })).toHaveCount(0);
  await expect(page.locator('#passwordRecoveryHelp')).toContainText('Password recovery is not available online.');
  await expect(page.locator('#passwordRecoveryHelp')).toContainText('Contact your CBS Support administrator');
  await expect(page.locator('#passwordRecoveryHelp a')).toHaveCount(0);
  await expect(page.locator('a[href="#"]')).toHaveCount(0);
});
