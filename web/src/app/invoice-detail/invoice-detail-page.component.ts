import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, input, signal } from '@angular/core';
import { DatePipe, DecimalPipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { DomSanitizer, SafeResourceUrl } from '@angular/platform-browser';
import {
  AuditTrail,
  Invoice,
  InvoicesApi,
  OUTCOME_LABELS,
  Outcome,
  PROCESSING,
  ReviewRequest,
  STATUS_LABELS,
  problemMessage,
} from '../invoices/invoices-api';

const SOURCE_LABELS = {
  agent: 'Agent recommendation',
  policyOverride: 'Policy overrode the agent',
  policyOnly: 'Policy only (agent did not finish)',
} as const;

@Component({
  selector: 'ip-invoice-detail-page',
  standalone: true,
  imports: [DatePipe, DecimalPipe, FormsModule, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './invoice-detail-page.component.html',
  styleUrl: './invoice-detail-page.component.css',
})
export class InvoiceDetailPageComponent implements OnInit {
  /** Bound from the route (:id) by withComponentInputBinding. */
  readonly id = input.required<string>();

  private readonly api = inject(InvoicesApi);
  private readonly sanitizer = inject(DomSanitizer);
  private readonly destroyRef = inject(DestroyRef);

  readonly invoice = signal<Invoice | null>(null);
  readonly audit = signal<AuditTrail | null>(null);
  readonly error = signal<string | null>(null);
  readonly busy = signal(false);
  readonly showOverride = signal(false);
  readonly showTrace = signal(false);

  overrideOutcome: Outcome = 'hold';
  note = '';

  readonly statusLabels = STATUS_LABELS;
  readonly outcomeLabels = OUTCOME_LABELS;
  readonly sourceLabels = SOURCE_LABELS;
  readonly outcomes: Outcome[] = ['approve', 'routeForApproval', 'hold', 'reject'];

  readonly processing = computed(() => {
    const i = this.invoice();
    return i !== null && PROCESSING.has(i.status);
  });
  readonly canReview = computed(() => {
    const i = this.invoice();
    return i !== null && i.decision != null && (i.status === 'awaitingReview' || i.status === 'investigationFailed');
  });
  readonly canApprove = computed(() => this.invoice()?.status === 'awaitingApproval');
  readonly isImage = computed(() => this.invoice()?.file.contentType.startsWith('image/') ?? false);
  readonly fileUrl = computed(() => this.api.fileUrl(this.id()));
  readonly pdfUrl = computed<SafeResourceUrl>(() =>
    // Same-origin URL built by the app itself (not user input), so trusting it is safe.
    this.sanitizer.bypassSecurityTrustResourceUrl(this.api.fileUrl(this.id())),
  );
  readonly toolSteps = computed(() => (this.invoice()?.agent?.steps ?? []).filter((s) => s.kind === 'tool'));

  private timer: ReturnType<typeof setTimeout> | null = null;

  constructor() {
    this.destroyRef.onDestroy(() => this.stopPolling());
  }

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.stopPolling();
    this.api.get(this.id()).subscribe({
      next: (invoice) => {
        this.invoice.set(invoice);
        this.loadAudit();
        if (PROCESSING.has(invoice.status)) {
          this.timer = setTimeout(() => this.load(), 2000);
        }
      },
      error: (err) => this.error.set(problemMessage(err, 'Could not load the invoice.')),
    });
  }

  allowed(outcome: Outcome): boolean {
    const required = this.invoice()?.decision?.policy.required;
    const rank: Record<Outcome, number> = { approve: 0, routeForApproval: 1, hold: 2, reject: 3 };
    return required === undefined || rank[outcome] >= rank[required];
  }

  confirm(): void {
    this.act({ action: 'confirm' });
  }

  submitOverride(): void {
    this.act({ action: 'override', outcome: this.overrideOutcome, note: this.note.trim() });
  }

  approve(): void {
    this.act({ action: 'approve', note: this.note.trim() || undefined });
  }

  reject(): void {
    this.act({ action: 'reject', note: this.note.trim() });
  }

  investigate(): void {
    this.api.investigate(this.id()).subscribe({
      next: (i) => {
        this.invoice.set(i);
        this.load();
      },
      error: (err) => this.error.set(problemMessage(err, 'Could not start the agent.')),
    });
  }

  private act(request: Omit<ReviewRequest, 'expectedVersion'>): void {
    const current = this.invoice();
    if (!current) return;
    this.busy.set(true);
    this.error.set(null);
    this.api.act(current.id, { ...request, expectedVersion: current.version }).subscribe({
      next: (updated) => {
        this.invoice.set(updated);
        this.busy.set(false);
        this.showOverride.set(false);
        this.note = '';
        this.loadAudit();
      },
      error: (err) => {
        this.error.set(problemMessage(err, 'The action failed.'));
        this.busy.set(false);
      },
    });
  }

  private loadAudit(): void {
    this.api.audit(this.id()).subscribe({
      next: (trail) => this.audit.set(trail),
      error: () => this.audit.set(null),
    });
  }

  private stopPolling(): void {
    if (this.timer !== null) {
      clearTimeout(this.timer);
      this.timer = null;
    }
  }
}
