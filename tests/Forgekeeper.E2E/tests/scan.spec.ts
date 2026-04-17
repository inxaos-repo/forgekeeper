import { test, expect } from '@playwright/test';

test.describe('Scanner', () => {
  test('scan status API returns data', async ({ request }) => {
    const response = await request.get('/api/v1/scan/status');
    expect(response.ok()).toBeTruthy();
    const body = await response.json();
    expect(body).toHaveProperty('isRunning');
    expect(body).toHaveProperty('status');
  });
});
