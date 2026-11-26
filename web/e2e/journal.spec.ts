import { test, expect } from '@playwright/test';

test('journal has separate tabs, searchable details, paging and isolated deletion', async ({
  page,
  request,
}) => {
  const errors: string[] = [];
  page.on('pageerror', (error) => errors.push(error.message));
  const name = `E2E HTTP журнал ${Date.now()}`;
  const response = await request.post('/admin/api/sessions', {
    data: {
      name,
      rules: [
        {
          id: 'capture',
          match: { path: '/capture' },
          response: { json: { result: 'journal-result', token: 'hidden-response' } },
        },
      ],
    },
  });
  const { id } = await response.json();
  try {
    for (let index = 0; index < 105; index++)
      await request.post(`/s/${id}/capture?number=${index}`, {
        data: { index, password: 'hidden-request' },
      });
    await page.goto('/sessions');
    await page.getByText(name, { exact: true }).click();
    const editor = page.getByLabel('Конфигурация сессии (JSON)');
    await editor.fill(JSON.stringify({ name: 'E2E HTTP несохранённая', rules: [] }));
    await page.getByRole('button', { name: 'Журнал запросов', exact: true }).click();
    await expect(editor).toBeHidden();
    await expect(page.locator('.requestJournal__row')).toHaveCount(50);
    await page.getByRole('button', { name: 'Загрузить ещё', exact: true }).click();
    await expect(page.locator('.requestJournal__row')).toHaveCount(100);
    await page.getByRole('button', { name: 'Загрузить ещё', exact: true }).click();
    await expect(page.locator('.requestJournal__row')).toHaveCount(105);
    await expect(page.getByRole('button', { name: 'Загрузить ещё', exact: true })).toBeHidden();
    await page.getByLabel('Метод', { exact: true }).fill('POST');
    await page.getByLabel('HTTP-статус', { exact: true }).fill('200');
    await page.getByRole('combobox', { name: 'Источник', exact: true }).selectOption('mock');
    await page.getByLabel('ID правила', { exact: true }).fill('capture');
    await page.getByLabel('Период с (местное время)', { exact: true }).fill('2000-01-01T00:00');
    await page.getByLabel('Период по (местное время)', { exact: true }).fill('2099-01-01T00:00');
    await page.getByLabel('Поиск в журнале').fill('number=104');
    await page.getByRole('button', { name: 'Применить фильтры', exact: true }).click();
    await expect(page.locator('.requestJournal__row')).toHaveCount(1);
    const filtered = await (
      await request.get(`/admin/api/sessions/${id}/requests?search=number%3D104`)
    ).json();
    const detailUrl = `**/admin/api/sessions/${id}/requests/${filtered[0].id}`;
    await page.route(detailUrl, (route) =>
      route.fulfill({ status: 503, json: { message: 'Подробности временно недоступны' } }),
    );
    await page.getByRole('button', { name: 'Подробнее', exact: true }).click();
    await expect(page.getByText('Подробности временно недоступны', { exact: false })).toBeVisible();
    await page.unroute(detailUrl);
    await page.getByRole('button', { name: 'Подробнее', exact: true }).click();
    await expect(page.getByText('journal-result', { exact: false })).toBeVisible();
    await expect(page.locator('.requestJournal__details')).toContainText('[REDACTED]');
    await expect(page.locator('.requestJournal__details')).not.toContainText('hidden-response');
    await expect(page.locator('.requestJournal__details')).not.toContainText('hidden-request');
    for (const theme of ['light', 'dark']) {
      if (theme === 'dark')
        await page.getByRole('button', { name: 'Тёмная тема', exact: true }).click();
      for (const width of [320, 820, 1440]) {
        await page.setViewportSize({ width, height: 900 });
        expect(
          await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth),
        ).toBeTruthy();
      }
    }
    await page.evaluate(() => (document.documentElement.style.fontSize = '200%'));
    await page.setViewportSize({ width: 320, height: 900 });
    expect(
      await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth),
    ).toBeTruthy();
    await page.evaluate(() => (document.documentElement.style.fontSize = ''));
    await page.setViewportSize({ width: 1440, height: 1000 });
    await page.bringToFront();
    await page.getByLabel('Поиск в журнале').focus();
    await expect(page.getByLabel('Поиск в журнале')).toBeFocused();
    await page.keyboard.press('Tab');
    await expect(page.getByLabel('Метод', { exact: true })).toBeFocused();
    await page.screenshot({ path: '../work/journal-desktop.png', fullPage: true });
    await page.getByRole('button', { name: 'Закрыть подробности', exact: true }).click();
    await expect(page.locator('.requestJournal__details')).toBeHidden();
    await page.getByLabel('Выбрать все загруженные').check();
    page.once('dialog', (dialog) => dialog.dismiss());
    await page.getByRole('button', { name: 'Удалить выбранные', exact: true }).click();
    await expect(page.locator('.requestJournal__row')).toHaveCount(1);
    page.once('dialog', (dialog) => dialog.accept());
    await page.getByRole('button', { name: 'Удалить выбранные', exact: true }).click();
    await expect(page.getByText('По заданным фильтрам запросов нет.')).toBeVisible();
    await page.getByRole('button', { name: 'Сбросить фильтры', exact: true }).click();
    await expect(page.locator('.requestJournal__row')).toHaveCount(50);
    await page.getByLabel('Поиск в журнале').fill('no-match');
    await page.getByRole('button', { name: 'Применить фильтры', exact: true }).click();
    page.once('dialog', (dialog) => dialog.accept());
    await page.getByRole('button', { name: 'Удалить весь журнал', exact: true }).click();
    await expect(page.getByText('Удалено записей: 104', { exact: true })).toBeVisible();
    expect(await (await request.get(`/admin/api/sessions/${id}/requests`)).json()).toEqual([]);
    expect((await (await request.get(`/admin/api/sessions/${id}`)).json()).state).toBe('started');
    await page.getByRole('button', { name: 'Конфигурация', exact: true }).click();
    await expect(editor).toHaveValue(/несохранённая/);
    await page.getByRole('button', { name: 'Сбросить изменения', exact: true }).click();
    expect(errors).toEqual([]);
  } finally {
    await request.delete(`/admin/api/sessions/${id}`).catch(() => undefined);
  }
});
