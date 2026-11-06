import { ChangeDetectionStrategy, Component } from '@angular/core';

@Component({
  selector: 'ip-inbox-page',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h1>AP Inbox</h1>
    <p class="empty">No invoices yet. Upload one to get started (coming in a later milestone).</p>
  `,
  styles: `
    h1 {
      margin: 0 0 1rem;
      font-size: 1.4rem;
    }
    .empty {
      color: var(--muted);
    }
  `,
})
export class InboxPageComponent {}
