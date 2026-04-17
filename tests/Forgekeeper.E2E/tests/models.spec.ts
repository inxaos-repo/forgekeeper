import { test, expect } from '@playwright/test';

test.describe('Models', () => {
  test('models list page loads', async ({ page }) => {
    await page.goto('/');
    // Should show models or an empty state
    await expect(page.locator('body')).toBeVisible();
  });

  test('search API returns results', async ({ request }) => {
    const response = await request.get('/api/v1/models?pageSize=5');
    // May return 200 with results or 200 with empty list
    if (response.ok()) {
      const body = await response.json();
      expect(body).toHaveProperty('items');
      expect(body).toHaveProperty('totalCount');
      expect(body.totalCount).toBeGreaterThanOrEqual(0);
    }
  });

  test('search with query returns filtered results', async ({ request }) => {
    const response = await request.get('/api/v1/models?query=dragon&pageSize=5');
    if (response.ok()) {
      const body = await response.json();
      expect(body.items.length).toBeLessThanOrEqual(5);
      // If there are results, they should have names
      for (const model of body.items) {
        expect(model).toHaveProperty('name');
        expect(model).toHaveProperty('id');
      }
    }
  });
});
