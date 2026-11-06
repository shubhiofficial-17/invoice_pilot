import { Routes } from '@angular/router';
import { InboxPageComponent } from './inbox/inbox-page.component';

export const routes: Routes = [
  { path: '', component: InboxPageComponent, title: 'AP Inbox · InvoicePilot' },
  { path: '**', redirectTo: '' },
];
