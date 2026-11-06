import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { RouterLink, RouterOutlet } from '@angular/router';

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
      justify-content: space-between;
      padding: 0.75rem 1.5rem;
      background: var(--surface);
      border-bottom: 1px solid var(--border);
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
  readonly apiStatus = signal<ApiStatus>('checking');

  constructor() {
    this.http.get<Health>('/api/health').subscribe({
      next: (h) => this.apiStatus.set(h.status === 'ok' ? 'up' : 'down'),
      error: () => this.apiStatus.set('down'),
    });
  }
}
