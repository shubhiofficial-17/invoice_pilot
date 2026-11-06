import { ChangeDetectionStrategy, Component, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { AuthService } from './auth.service';
import { safeReturnUrl } from './auth.guard';
import { problemMessage } from '../invoices/invoices-api';

@Component({
  selector: 'ip-sign-in-page',
  standalone: true,
  imports: [FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <form class="card signin" (ngSubmit)="submit()" [attr.aria-label]="'Sign in'">
      <h1>Sign in</h1>
      <label>Email <input name="email" type="email" autocomplete="username" required [(ngModel)]="email" data-testid="email" /></label>
      <label>Password <input name="password" type="password" autocomplete="current-password" required [(ngModel)]="password" data-testid="password" /></label>
      @if (error()) {
        <p class="error" role="alert" data-testid="error">{{ error() }}</p>
      }
      <button class="btn primary" type="submit" [disabled]="busy()" data-testid="submit">Sign in</button>
      <p class="muted small">
        Demo users: asha.rao@… (AP clerk), priya.nair@… / rahul.mehta@… (buyers), ap.manager@…, finance.controller@…
        (all &#64;invoicepilot.example).
      </p>
    </form>
  `,
  styles: `
    .signin { max-width: 24rem; margin: 3rem auto; display: grid; gap: 0.75rem; }
    .signin h1 { margin: 0; font-size: 1.3rem; }
    label { display: grid; gap: 0.25rem; font-size: 0.9rem; }
    input { font: inherit; padding: 0.45rem; }
    .small { font-size: 0.8rem; }
  `,
})
export class SignInPageComponent {
  readonly returnUrl = input<string | undefined>();

  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  email = '';
  password = '';

  submit(): void {
    this.busy.set(true);
    this.error.set(null);
    this.auth.signIn(this.email.trim(), this.password).subscribe({
      next: () => {
        this.busy.set(false);
        void this.router.navigateByUrl(safeReturnUrl(this.returnUrl()));
      },
      error: (err) => {
        this.busy.set(false);
        this.error.set(problemMessage(err, 'Sign-in failed.'));
      },
    });
  }
}
