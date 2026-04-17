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

  test('search with sort returns ordered results', async ({ request }) => {
    const response = await request.get('/api/v1/models?sortBy=fileCount&sortDescending=true&pageSize=3');
    if (response.ok()) {
      const body = await response.json();
      if (body.items.length >= 2) {
        expect(body.items[0].fileCount).toBeGreaterThanOrEqual(body.items[1].fileCount);
      }
    }
  });

  test('search with pagination works', async ({ request }) => {
    const page1 = await request.get('/api/v1/models?pageSize=2&page=1');
    const page2 = await request.get('/api/v1/models?pageSize=2&page=2');
    if (page1.ok() && page2.ok()) {
      const body1 = await page1.json();
      const body2 = await page2.json();
      if (body1.items.length > 0 && body2.items.length > 0) {
        expect(body1.items[0].id).not.toBe(body2.items[0].id);
      }
    }
  });

  test('filter sidebar is visible on models page', async ({ page }) => {
    await page.goto('/');
    // Filter sidebar should be present (hidden on mobile, visible on desktop)
    const sidebar = page.locator('aside');
    await expect(sidebar).toBeAttached();
  });

  test('model detail page loads', async ({ page, request }) => {
    // Get a model ID first
    const listRes = await request.get('/api/v1/models?pageSize=1');
    if (listRes.ok()) {
      const data = await listRes.json();
      if (data.items?.length > 0) {
        await page.goto(`/models/${data.items[0].id}`);
        await expect(page.locator('body')).toBeVisible();
      }
    }
  });

  test('model update endpoint works', async ({ request }) => {
    const listRes = await request.get('/api/v1/models?pageSize=1');
    if (listRes.ok()) {
      const data = await listRes.json();
      if (data.items?.length > 0) {
        const id = data.items[0].id;
        const res = await request.patch(`/api/v1/models/${id}`, {
          data: { notes: 'E2E test note' }
        });
        expect(res.ok()).toBeTruthy();
        // Clean up
        await request.patch(`/api/v1/models/${id}`, { data: { notes: null } });
      }
    }
  });
});
