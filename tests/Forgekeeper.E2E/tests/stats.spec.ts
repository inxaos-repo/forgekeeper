import { test, expect } from '@playwright/test';

test.describe('Stats', () => {
  test('stats API returns data', async ({ request }) => {
    const response = await request.get('/api/v1/stats');
    expect(response.ok()).toBeTruthy();
    const body = await response.json();
    expect(body).toHaveProperty('totalModels');
    expect(body).toHaveProperty('totalCreators');
    expect(body).toHaveProperty('totalFiles');
    expect(body).toHaveProperty('totalSizeBytes');
    expect(body.totalModels).toBeGreaterThanOrEqual(0);
  });

  test('stats page loads', async ({ page }) => {
    await page.goto('/stats');
    await expect(page.locator('body')).toBeVisible();
  });
});
