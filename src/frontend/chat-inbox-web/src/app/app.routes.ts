import { Routes } from '@angular/router';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'inbox' },
  {
    path: 'inbox',
    loadComponent: () => import('./inbox/inbox.component').then((component) => component.InboxComponent),
  },
];
