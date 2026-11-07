import { Routes } from '@angular/router';
import { InboxPageComponent } from './inbox/inbox-page.component';
import { InvoiceDetailPageComponent } from './invoice-detail/invoice-detail-page.component';

export const routes: Routes = [
  { path: '', component: InboxPageComponent, title: 'AP Inbox · InvoicePilot' },
  { path: 'invoices/:id', component: InvoiceDetailPageComponent, title: 'Invoice · InvoicePilot' },
  { path: '**', redirectTo: '' },
];
