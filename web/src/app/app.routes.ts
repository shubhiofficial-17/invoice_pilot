import { Routes } from '@angular/router';
import { authGuard } from './auth/auth.guard';
import { SignInPageComponent } from './auth/sign-in-page.component';
import { InboxPageComponent } from './inbox/inbox-page.component';
import { InvoiceDetailPageComponent } from './invoice-detail/invoice-detail-page.component';

export const routes: Routes = [
  { path: 'sign-in', component: SignInPageComponent, title: 'Sign in · InvoicePilot' },
  { path: '', component: InboxPageComponent, title: 'AP Inbox · InvoicePilot', canActivate: [authGuard] },
  { path: 'invoices/:id', component: InvoiceDetailPageComponent, title: 'Invoice · InvoicePilot', canActivate: [authGuard] },
  { path: '**', redirectTo: '' },
];
