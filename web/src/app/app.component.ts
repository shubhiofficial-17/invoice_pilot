import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Router, RouterLink, RouterOutlet } from '@angular/router';
import { AuthService } from './auth/auth.service';

interface Health {
  status: string;
  service: string;
}

export type ApiStatus = 'checking' | 'up' | 'down';

@Component({
  selector: 'ip-root',
  standalone: true,
  imports: [RouterOutlet, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="top">
      <a class="brand" routerLink="/">InvoicePilot</a>
      @if (auth.me(); as user) {
        <span class="user" data-testid="user">{{ user.name }}</span>
        <button type="button" class="link" data-testid="sign-out" (click)="signOut()">Sign out</button>
      }
      <span class="status" data-testid="api-status" [class]="apiStatus()">
        @switch (apiStatus()) {
          @case ('up') { API online }
          @case ('down') { API offline }
          @default { Checking API… }
        }
      </span>
    </header>
    <main>
      <router-outlet />
    </main>
  `,
  styles: `
    .top {
      display: flex;
      align-items: center;
      gap: 1rem;
      padding: 0.75rem 1.5rem;
      background: var(--surface);
      border-bottom: 1px solid var(--border);
    }
    .user {
      margin-left: auto;
      margin-right: 0.5rem;
      font-size: 0.85rem;
    }
    .brand {
      font-weight: 700;
      color: var(--text);
      text-decoration: none;
    }
    .status {
      font-size: 0.85rem;
      color: var(--muted);
    }
    .status.up {
      color: var(--ok);
    }
    .status.down {
      color: var(--bad);
    }
    main {
      padding: 1.5rem;
    }
  `,
})
export class AppComponent {
  private readonly http = inject(HttpClient);
  private readonly router = inject(Router);
  readonly auth = inject(AuthService);
  readonly apiStatus = signal<ApiStatus>('checking');

  signOut(): void {
    this.auth.signOut().subscribe(() => void this.router.navigate(['/sign-in']));
  }

  constructor() {
    this.http.get<Health>('/api/health').subscribe({
      next: (h) => this.apiStatus.set(h.status === 'ok' ? 'up' : 'down'),
      error: () => this.apiStatus.set('down'),
    });
  }
}
