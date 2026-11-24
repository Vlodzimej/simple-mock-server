import { test, expect } from '@playwright/test';

test('home opens the session workspace with the product identity', async ({ page }) => {
  const administrativeReads: string[] = [];
  page.on('request', (request) => {
    const path = new URL(request.url()).pathname;
    if (path.startsWith('/admin/api/')) administrativeReads.push(path);
  });
  await page.goto('/');
  await expect(page).toHaveTitle('Simple Mock Server');
  await expect(page).toHaveURL(/\/sessions$/);
  await expect(page.locator('.shell__name')).toHaveText('Simple Mock Server');
  await expect(page.locator('.shell__navigation a')).toHaveCount(1);
  await expect(page.getByRole('button', { name: 'Новая HTTP-сессия', exact: true })).toBeVisible();
  expect(administrativeReads).toEqual(['/admin/api/sessions']);
});
