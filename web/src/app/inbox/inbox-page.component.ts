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
      <button type="button" class="link" data-testid="refresh" (click)="load()">Refresh</button>
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
          <tr><th>File</th><th>Supplier</th><th>Invoice #</th><th class="num">Total</th><th>Status</th><th>Received</th></tr>
        </thead>
        <tbody>
          @for (invoice of invoices(); track invoice.id) {
            <tr data-testid="invoice-row">
              <td><a [href]="api.fileUrl(invoice.id)" target="_blank" rel="noopener">{{ invoice.file.fileName }}</a></td>
              <td>{{ invoice.extraction?.data?.supplierName ?? '—' }}</td>
              <td>{{ invoice.extraction?.data?.invoiceNumber ?? '—' }}</td>
              <td class="num">
                @if (invoice.extraction?.data?.total != null) {
                  {{ invoice.extraction!.data.currency }} {{ invoice.extraction!.data.total | number: '1.2-2' }}
                } @else { — }
              </td>
              <td>
                <span class="badge" [class]="invoice.status" [attr.title]="invoice.failureReason">{{ label(invoice.status) }}</span>
                @if (invoice.extraction?.issues?.length) {
                  <span class="issues" data-testid="issues" [attr.title]="issueText(invoice)">{{ invoice.extraction!.issues.length }} issue(s)</span>
                }
                @if (invoice.status === 'extractionFailed') {
                  <button type="button" class="link" data-testid="retry" (click)="retry(invoice)">Retry</button>
                }
              </td>
              <td>{{ invoice.receivedAt | date: 'd MMM y, HH:mm' }}</td>
            </tr>
          }
        </tbody>
      </table>
    }
  `,
  styles: `
    .head { display: flex; align-items: center; gap: 1rem; margin-bottom: 1rem; }
    h1 { margin: 0 auto 0 0; font-size: 1.4rem; }
    .link { background: none; border: 0; color: var(--accent); cursor: pointer; padding: 0 0.25rem; }
    .issues { margin-left: 0.4rem; font-size: 0.8rem; color: var(--warn); }
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
    this.load();
  }

  load(): void {
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

  issueText(invoice: Invoice): string {
    return (invoice.extraction?.issues ?? []).map((i) => i.message).join('\n');
  }

  retry(invoice: Invoice): void {
    this.error.set(null);
    this.api.extract(invoice.id).subscribe({
      next: (updated) => this.replace(updated),
      error: (err) => this.error.set(problemMessage(err, 'Could not retry.')),
    });
  }

  private replace(updated: Invoice): void {
    this.invoices.update((list) => list.map((i) => (i.id === updated.id ? updated : i)));
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
