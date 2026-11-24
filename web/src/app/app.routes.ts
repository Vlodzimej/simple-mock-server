import { Routes } from '@angular/router';
import { unsavedGuard } from './core/unsaved-guard';

export const routes: Routes = [
  {
    path: 'sessions',
    loadComponent: () => import('./sessions/sessions').then((module) => module.Sessions),
    canDeactivate: [unsavedGuard],
  },
  { path: '', pathMatch: 'full', redirectTo: 'sessions' },
  { path: '**', redirectTo: 'sessions' },
];
