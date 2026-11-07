import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { DatePipe, DecimalPipe } from '@angular/common';
import { Invoice, InvoiceStatus, InvoicesApi, problemMessage } from '../invoices/invoices-api';

const STATUS_LABELS: Record<InvoiceStatus, string> = {
  received: 'Received',
  extracting: 'Reading…',
  extracted: 'Read',
  extractionFailed: 'Could not read',
};

@Component({
  selector: 'ip-inbox-page',
  standalone: true,
  imports: [DatePipe, DecimalPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="head">
      <h1>AP Inbox</h1>
      <label class="upload" [class.busy]="uploading()">
        {{ uploading() ? 'Uploading…' : 'Upload invoice' }}
        <input
          type="file"
          data-testid="upload-input"
          accept=".pdf,.png,.jpg,.jpeg,application/pdf,image/png,image/jpeg"
          [disabled]="uploading()"
          (change)="onFileChosen($event)"
        />
      </label>
    </div>

    @if (error()) {
      <p class="error" role="alert" data-testid="error">{{ error() }}</p>
    }

    @if (loading()) {
      <p class="muted">Loading…</p>
    } @else if (invoices().length === 0) {
      <p class="muted" data-testid="empty">No invoices yet. Upload a PDF, PNG or JPEG invoice to get started.</p>
    } @else {
      <table>
        <thead>
          <tr><th>File</th><th>Status</th><th>Received</th><th class="num">Size</th></tr>
        </thead>
        <tbody>
          @for (invoice of invoices(); track invoice.id) {
            <tr data-testid="invoice-row">
              <td><a [href]="api.fileUrl(invoice.id)" target="_blank" rel="noopener">{{ invoice.file.fileName }}</a></td>
              <td><span class="badge" [class]="invoice.status">{{ label(invoice.status) }}</span></td>
              <td>{{ invoice.receivedAt | date: 'd MMM y, HH:mm' }}</td>
              <td class="num">{{ invoice.file.size / 1024 | number: '1.0-0' }} KB</td>
            </tr>
          }
        </tbody>
      </table>
    }
  `,
  styles: `
    .head { display: flex; align-items: center; justify-content: space-between; margin-bottom: 1rem; }
    h1 { margin: 0; font-size: 1.4rem; }
    .upload { background: var(--accent); color: #fff; padding: 0.5rem 0.9rem; border-radius: 6px; cursor: pointer; }
    .upload.busy { opacity: 0.6; cursor: progress; }
    .upload input { display: none; }
    .muted { color: var(--muted); }
    .error { color: var(--bad); }
    table { width: 100%; border-collapse: collapse; background: var(--surface); border: 1px solid var(--border); }
    th, td { text-align: left; padding: 0.55rem 0.75rem; border-bottom: 1px solid var(--border); }
    th { font-size: 0.8rem; color: var(--muted); font-weight: 600; }
    .num { text-align: right; }
    .badge { font-size: 0.8rem; padding: 0.1rem 0.5rem; border-radius: 999px; background: var(--bg); }
    .badge.extracted { color: var(--ok); }
    .badge.extractionFailed { color: var(--bad); }
  `,
})
export class InboxPageComponent implements OnInit {
  readonly api = inject(InvoicesApi);

  readonly invoices = signal<Invoice[]>([]);
  readonly loading = signal(true);
  readonly uploading = signal(false);
  readonly error = signal<string | null>(null);

  ngOnInit(): void {
    this.api.list().subscribe({
      next: (list) => {
        this.invoices.set(list);
        this.loading.set(false);
      },
      error: (err) => {
        this.error.set(problemMessage(err, 'Could not load invoices.'));
        this.loading.set(false);
      },
    });
  }

  label(status: InvoiceStatus): string {
    return STATUS_LABELS[status] ?? status;
  }

  onFileChosen(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = ''; // lets the same file be chosen again
    if (file) this.upload(file);
  }

  upload(file: File): void {
    this.uploading.set(true);
    this.error.set(null);
    this.api.upload(file).subscribe({
      next: (invoice) => {
        // Re-uploading a file returns the invoice that already exists; show it once, at the top.
        this.invoices.update((list) => [invoice, ...list.filter((i) => i.id !== invoice.id)]);
        this.uploading.set(false);
      },
      error: (err) => {
        this.error.set(problemMessage(err, 'Upload failed.'));
        this.uploading.set(false);
      },
    });
  }
}
