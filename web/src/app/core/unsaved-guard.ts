import { CanDeactivateFn } from '@angular/router';

/** Контракт редактора с несохранёнными изменениями. */
export interface IUnsavedEditor {
  canLeave(): boolean;
}
/** Защита изменённых форм при переходе между страницами. */
export const unsavedGuard: CanDeactivateFn<IUnsavedEditor> = (component) => component.canLeave();
