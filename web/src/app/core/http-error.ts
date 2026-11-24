import { HttpErrorResponse } from '@angular/common/http';

/** Текст ошибки для отображения в панели. */
export function messageOf(error: unknown): string {
  if (error instanceof HttpErrorResponse) {
    const body: unknown = error.error;
    if (
      typeof body === 'object' &&
      body !== null &&
      'message' in body &&
      typeof body.message === 'string'
    )
      return body.message;
    if (error.status === 0)
      return 'Сервер недоступен. Проверьте запуск backend и повторите запрос.';
    if (error.status === 404) return 'Запись не найдена. Обновите список.';
    return `Не удалось выполнить запрос (HTTP ${error.status}). Повторите действие.`;
  }
  return 'Не удалось выполнить действие. Повторите попытку.';
}
