import { test, expect } from '@playwright/test';

test.describe('Health & Basics', () => {
  test('health endpoint returns healthy', async ({ request }) => {
    const response = await request.get('/health');
    expect(response.ok()).toBeTruthy();
    const body = await response.json();
    expect(body.status).toBe('healthy');
  });

  test('homepage loads', async ({ page }) => {
    await page.goto('/');
    await expect(page).toHaveTitle(/Forgekeeper/);
  });

  test('navbar is visible', async ({ page }) => {
    await page.goto('/');
    await expect(page.locator('nav')).toBeVisible();
  });
});
