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

  test('create and delete source via API', async ({ request }) => {
    // Create
    const createRes = await request.post('/api/v1/sources', {
      data: { slug: 'e2e-test', name: 'E2E Test Source', basePath: '/tmp/e2e-test', adapterType: 'GenericSourceAdapter', autoScan: false }
    });
    expect(createRes.status()).toBe(201);

    // Verify
    const getRes = await request.get('/api/v1/sources/e2e-test');
    expect(getRes.ok()).toBeTruthy();

    // Delete
    const delRes = await request.delete('/api/v1/sources/e2e-test');
    expect(delRes.status()).toBe(204);
  });
});
