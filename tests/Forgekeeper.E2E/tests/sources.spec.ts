import { test, expect } from '@playwright/test';

test.describe('Sources', () => {
  test('sources API returns list', async ({ request }) => {
    const response = await request.get('/api/v1/sources');
    expect(response.ok()).toBeTruthy();
    const body = await response.json();
    expect(Array.isArray(body)).toBeTruthy();
  });

  test('sources page loads', async ({ page }) => {
    await page.goto('/sources');
    await expect(page.locator('body')).toBeVisible();
  });
});
