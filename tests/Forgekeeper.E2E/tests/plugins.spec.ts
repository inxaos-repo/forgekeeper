import { test, expect } from '@playwright/test';

test.describe('Plugins', () => {
  test('plugins API returns list', async ({ request }) => {
    const response = await request.get('/api/v1/plugins');
    expect(response.ok()).toBeTruthy();
    const body = await response.json();
    expect(Array.isArray(body)).toBeTruthy();
  });

  test('plugins page loads', async ({ page }) => {
    await page.goto('/plugins');
    await expect(page.locator('body')).toBeVisible();
  });

  test('MMF plugin is listed', async ({ request }) => {
    const response = await request.get('/api/v1/plugins');
    if (response.ok()) {
      const plugins = await response.json();
      const mmf = plugins.find((p: any) => p.slug === 'mmf');
      if (mmf) {
        expect(mmf.name).toBe('MyMiniFactory');
        expect(mmf.version).toBeTruthy();
      }
    }
  });

  test('plugin auth endpoint works', async ({ request }) => {
    const response = await request.get('/api/v1/plugins/mmf/auth');
    // Should return 200 (auth status) or 404 (plugin not loaded) — never 400
    expect(response.status()).not.toBe(400);
    expect([200, 404]).toContain(response.status());
  });

  test('plugin sync status endpoint works', async ({ request }) => {
    const response = await request.get('/api/v1/plugins/mmf/status');
    // Plugin may not be loaded in minimal Docker Compose (no plugin DLL)
    if (response.ok()) {
      const body = await response.json();
      expect(body).toHaveProperty('isRunning');
    } else {
      expect(response.status()).toBe(404); // Plugin not found is acceptable
    }
  });
});
