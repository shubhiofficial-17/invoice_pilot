import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { DatePipe, DecimalPipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { Invoice, InvoiceStatus, InvoicesApi, PROCESSING, STATUS_LABELS, problemMessage } from '../invoices/invoices-api';

export type InboxFilter = 'all' | 'review' | 'processing' | 'done';

const REVIEW: ReadonlySet<InvoiceStatus> = new Set<InvoiceStatus>([
  'awaitingReview', 'awaitingApproval', 'investigationFailed', 'extractionFailed',
]);


@Component({
  selector: 'ip-inbox-page',
  standalone: true,
  imports: [DatePipe, DecimalPipe, RouterLink],
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

    <nav class="filters">
      @for (f of filters; track f.key) {
        <button type="button" class="chip" [class.on]="filter() === f.key" [attr.data-testid]="'filter-' + f.key" (click)="filter.set(f.key)">
          {{ f.label }} <span class="count">{{ count(f.key) }}</span>
        </button>
      }
    </nav>

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
          @for (invoice of visible(); track invoice.id) {
            <tr data-testid="invoice-row">
              <td><a [routerLink]="['/invoices', invoice.id]" data-testid="open">{{ invoice.file.fileName }}</a></td>
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
                @if (invoice.status === 'extractionFailed' || invoice.status === 'investigationFailed') {
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
    .filters { display: flex; gap: 0.5rem; margin-bottom: 0.75rem; }
    .chip { border: 1px solid var(--border); background: var(--surface); border-radius: 999px; padding: 0.25rem 0.75rem; cursor: pointer; font: inherit; }
    .chip.on { border-color: var(--accent); color: var(--accent); }
    .count { color: var(--muted); font-size: 0.8rem; }
  `,
})
export class InboxPageComponent implements OnInit {
  readonly api = inject(InvoicesApi);

  readonly invoices = signal<Invoice[]>([]);
  readonly loading = signal(true);
  readonly uploading = signal(false);
  readonly error = signal<string | null>(null);
  readonly filter = signal<InboxFilter>('all');
  readonly filters: { key: InboxFilter; label: string }[] = [
    { key: 'all', label: 'All' },
    { key: 'review', label: 'Needs a person' },
    { key: 'processing', label: 'In progress' },
    { key: 'done', label: 'Done' },
  ];
  readonly visible = computed(() => this.invoices().filter((i) => this.matches(this.filter(), i.status)));

  private timer: ReturnType<typeof setTimeout> | null = null;

  constructor() {
    inject(DestroyRef).onDestroy(() => this.stopPolling());
  }

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.stopPolling();
    this.api.list().subscribe({
      next: (list) => {
        this.invoices.set(list);
        this.loading.set(false);
        // Keep the list fresh while the pipeline is working on something.
        if (list.some((i) => PROCESSING.has(i.status))) {
          this.timer = setTimeout(() => this.load(), 3000);
        }
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

  count(filter: InboxFilter): number {
    return this.invoices().filter((i) => this.matches(filter, i.status)).length;
  }

  private matches(filter: InboxFilter, status: InvoiceStatus): boolean {
    switch (filter) {
      case 'review':
        return REVIEW.has(status);
      case 'processing':
        return PROCESSING.has(status);
      case 'done':
        return !REVIEW.has(status) && !PROCESSING.has(status);
      default:
        return true;
    }
  }

  private stopPolling(): void {
    if (this.timer !== null) {
      clearTimeout(this.timer);
      this.timer = null;
    }
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
        if (PROCESSING.has(invoice.status) && this.timer === null) {
          this.timer = setTimeout(() => this.load(), 3000);
        }
      },
      error: (err) => {
        this.error.set(problemMessage(err, 'Upload failed.'));
        this.uploading.set(false);
      },
    });
  }
}
