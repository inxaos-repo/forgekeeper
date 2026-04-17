import { test, expect } from '@playwright/test';

test.describe('MCP', () => {
  test('MCP tools endpoint returns tools', async ({ request }) => {
    const response = await request.get('/mcp/tools');
    expect(response.ok()).toBeTruthy();
    const body = await response.json();
    expect(body).toHaveProperty('tools');
    expect(body.tools.length).toBeGreaterThan(0);
  });

  test('MCP search tool works', async ({ request }) => {
    const response = await request.post('/mcp/invoke', {
      data: { tool: 'search', arguments: { query: 'test', pageSize: 1 } }
    });
    expect(response.ok()).toBeTruthy();
  });

  test('MCP stats tool works', async ({ request }) => {
    const response = await request.post('/mcp/invoke', {
      data: { tool: 'stats', arguments: {} }
    });
    expect(response.ok()).toBeTruthy();
  });
});
