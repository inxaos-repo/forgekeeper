import { test, expect } from '@playwright/test';

test.describe('Creators', () => {
  test('creators API returns list', async ({ request }) => {
    const response = await request.get('/api/v1/creators');
    expect(response.ok()).toBeTruthy();
    const body = await response.json();
    // Creators list is now paginated — check for PaginatedResult shape
    expect(body.items !== undefined || Array.isArray(body)).toBeTruthy();
  });

  test('creators page loads', async ({ page }) => {
    await page.goto('/creators');
    await expect(page.locator('body')).toBeVisible();
  });
});
