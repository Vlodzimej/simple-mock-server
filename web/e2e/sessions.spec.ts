import { test, expect } from '@playwright/test';

test.beforeEach(async ({ request }) => {
  const response = await request.get('/admin/api/sessions');
  if (response.ok()) {
    for (const session of await response.json()) {
      if (session.name.startsWith('E2E HTTP'))
        await request.delete(`/admin/api/sessions/${session.id}`);
    }
  }
});

test('session editor configures arbitrary paths, journal, reset and portable configuration', async ({
  page,
  request,
}) => {
  const errors: string[] = [];
  page.on('pageerror', (error) => errors.push(error.message));
  await page.goto('/sessions');
  await page.getByRole('button', { name: 'Новая HTTP-сессия', exact: true }).click();
  const editor = page.getByLabel('Конфигурация сессии (JSON)');
  const configuration = {
    name: 'E2E HTTP задача',
    upstreamBaseUrl: '',
    proxyEnabled: false,
    rules: [
      {
        id: 'orders',
        match: { method: 'GET', path: '/v3/orders/{id}' },
        response: { status: 200, json: { success: true } },
      },
    ],
    protectedPaths: ['/v3/orders/{id}'],
  };
  await editor.fill('{broken');
  await page.getByRole('button', { name: 'Сохранить сессию', exact: true }).click();
  await expect(page.getByText('Некорректный JSON. Проверьте конфигурацию.')).toBeVisible();
  await editor.fill(JSON.stringify(configuration, null, 2));
  await page.getByRole('button', { name: 'Сохранить сессию', exact: true }).click();
  await expect(page.getByText('Сессия сохранена', { exact: true })).toBeVisible();
  const base = await page.getByLabel('Адрес подключения приложения').inputValue();
  expect((await request.get(`${base}/v3/orders/42`)).status()).toBe(200);
  await page.getByRole('button', { name: 'Обновить запросы', exact: true }).click();
  await expect(
    page.locator('.sessions__request').filter({ hasText: '/v3/orders/42' }),
  ).toContainText('orders');
  await editor.fill(JSON.stringify({ ...configuration, name: 'E2E HTTP несохранённая' }));
  page.once('dialog', (dialog) => dialog.dismiss());
  await page.getByRole('button', { name: 'Новая HTTP-сессия', exact: true }).click();
  await expect(editor).toBeVisible();
  await page.getByRole('button', { name: 'Сбросить изменения', exact: true }).click();
  await expect(editor).toHaveValue(/E2E HTTP задача/);
  const downloadPromise = page.waitForEvent('download');
  await page.getByRole('button', { name: 'Экспорт JSON', exact: true }).click();
  const download = await downloadPromise;
  expect(download.suggestedFilename()).toMatch(/^session-.*\.json$/);
  await page.getByLabel('Импорт сессии из JSON').setInputFiles({
    name: 'session.json',
    mimeType: 'application/json',
    buffer: Buffer.from(JSON.stringify({ ...configuration, name: 'E2E HTTP импорт' })),
  });
  await expect(page.getByText('Сессия импортирована', { exact: true })).toBeVisible();
  await expect(editor).toHaveValue(/E2E HTTP импорт/);
  await page.getByRole('button', { name: 'Сбросить состояние и журнал', exact: true }).click();
  await expect(
    page.getByText('Запросов пока нет. Выполните вызов через адрес этой сессии.'),
  ).toBeVisible();
  for (const theme of ['light', 'dark']) {
    if (theme === 'dark')
      await page.getByRole('button', { name: 'Тёмная тема', exact: true }).click();
    for (const width of [320, 820, 1440]) {
      await page.setViewportSize({ width, height: 900 });
      expect(
        await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth),
      ).toBeTruthy();
    }
    await expect(editor).toBeEnabled();
    await editor.focus();
    await expect(editor).toBeFocused();
    await page.keyboard.press('Tab');
    await expect(page.getByRole('button', { name: 'Сохранить сессию', exact: true })).toBeFocused();
  }
  await page.evaluate(() => (document.documentElement.style.fontSize = '200%'));
  await page.setViewportSize({ width: 320, height: 900 });
  expect(
    await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth),
  ).toBeTruthy();
  await page.evaluate(() => (document.documentElement.style.fontSize = ''));
  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.screenshot({ path: '../work/sessions-desktop.png', fullPage: true });
  page.once('dialog', (dialog) => dialog.accept());
  await page.getByRole('button', { name: 'Удалить сессию', exact: true }).click();
  await expect(page.getByText('Сессия удалена', { exact: true })).toBeVisible();
  expect(errors).toEqual([]);
});

test('session editor retains invalid configuration and handles load failures', async ({ page }) => {
  await page.route('**/admin/api/sessions', (route) =>
    route.fulfill({ status: 503, json: { message: 'Сессии временно недоступны' } }),
  );
  await page.goto('/sessions');
  await expect(page.getByText('Сессии временно недоступны')).toBeVisible();
  await page.unroute('**/admin/api/sessions');
  await page.getByRole('button', { name: 'Обновить список сессий', exact: true }).click();
  await page.getByRole('button', { name: 'Новая HTTP-сессия', exact: true }).click();
  const editor = page.getByLabel('Конфигурация сессии (JSON)');
  await editor.fill(
    JSON.stringify({
      name: 'E2E HTTP invalid',
      rules: [{ id: 'bad', match: { path: 'relative' } }],
    }),
  );
  await page.getByRole('button', { name: 'Сохранить сессию', exact: true }).click();
  await expect(
    page.getByText('Нужен путь от / без query и fragment', { exact: true }),
  ).toBeVisible();
  await expect(editor).toHaveValue(/relative/);
});

test('session selection loads current configuration and retains edits during a failed save', async ({
  page,
  request,
}) => {
  const response = await request.post('/admin/api/sessions', {
    data: { name: 'E2E HTTP выбор', rules: [] },
  });
  const { id } = await response.json();
  await page.goto('/sessions');
  await page.getByRole('button', { name: /E2E HTTP выбор/ }).click();
  const editor = page.getByLabel('Конфигурация сессии (JSON)');
  await expect(editor).toHaveValue(/E2E HTTP выбор/);
  await editor.fill(JSON.stringify({ name: 'E2E HTTP правка', rules: [] }));
  await page.route(`**/admin/api/sessions/${id}`, async (route) => {
    if (route.request().method() === 'PUT')
      await route.fulfill({ status: 503, json: { message: 'Сохранение сессии недоступно' } });
    else await route.continue();
  });
  await page.getByRole('button', { name: 'Сохранить сессию', exact: true }).click();
  await expect(page.getByText('Сохранение сессии недоступно')).toBeVisible();
  await expect(editor).toHaveValue(/E2E HTTP правка/);
  await page.unroute(`**/admin/api/sessions/${id}`);
  await page.getByRole('button', { name: 'Сохранить сессию', exact: true }).click();
  await expect(page.getByText('Сессия сохранена', { exact: true })).toBeVisible();
  await page.route(`**/admin/api/sessions/${id}/requests`, (route) =>
    route.fulfill({ status: 503, json: { message: 'Журнал сессии недоступен' } }),
  );
  await page.getByRole('button', { name: 'Обновить запросы', exact: true }).click();
  await expect(page.getByText('Журнал сессии недоступен')).toBeVisible();
  await page.unroute(`**/admin/api/sessions/${id}/requests`);
  await page.getByRole('button', { name: 'Обновить запросы', exact: true }).click();
  await expect(
    page.getByText('Запросов пока нет. Выполните вызов через адрес этой сессии.'),
  ).toBeVisible();
});
