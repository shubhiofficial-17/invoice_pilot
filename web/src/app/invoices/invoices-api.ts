import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Observable } from 'rxjs';

export type InvoiceStatus =
  | 'received'
  | 'extracting'
  | 'extracted'
  | 'extractionFailed'
  | 'investigating'
  | 'awaitingReview'
  | 'investigationFailed'
  | 'awaitingApproval'
  | 'approved'
  | 'onHold'
  | 'rejected';

export type Outcome = 'approve' | 'routeForApproval' | 'hold' | 'reject';

export interface AgentStep {
  index: number;
  kind: 'model' | 'tool';
  toolName: string | null;
  arguments: string | null;
  output: string;
  isError: boolean;
  promptTokens: number;
  completionTokens: number;
  durationMs: number;
  at: string;
}

export interface AgentRun {
  model: string;
  status: 'completed' | 'failed';
  proposal: {
    outcome: Outcome;
    reason: string;
    findingCodes: string[];
    supplierEmail: { to: string; subject: string; body: string } | null;
  } | null;
  failureReason: string | null;
  steps: AgentStep[];
  usage: { promptTokens: number; completionTokens: number };
  modelCalls: number;
  startedAt: string;
  finishedAt: string;
}

export interface InvoiceFile {
  fileName: string;
  contentType: string;
  size: number;
  sha256: string;
}

export interface ExtractedLine {
  description: string;
  sku: string | null;
  quantity: number;
  unitPrice: number;
  taxRate: number | null;
  amount: number | null;
}

export interface ExtractedInvoice {
  supplierName: string | null;
  supplierTaxId: string | null;
  invoiceNumber: string | null;
  invoiceDate: string | null;
  dueDate: string | null;
  poNumber: string | null;
  currency: string | null;
  lines: ExtractedLine[];
  subtotal: number | null;
  taxTotal: number | null;
  total: number | null;
}

export interface ExtractionIssue {
  code: string;
  message: string;
}

export interface Extraction {
  data: ExtractedInvoice;
  issues: ExtractionIssue[];
  source: 'pdf-text' | 'image';
  model: string;
  attempts: number;
  usage: { promptTokens: number; completionTokens: number };
  at: string;
}

export interface Invoice {
  id: string;
  status: InvoiceStatus;
  file: InvoiceFile;
  receivedAt: string;
  updatedAt: string;
  version: number;
  extraction: Extraction | null;
  failureReason: string | null;
  agent: AgentRun | null;
  match?: MatchReport | null;
  decision?: Decision | null;
  actions?: HumanAction[] | null;
  outbox?: OutboxEmail | null;
  booking?: string | null;
}

export type Severity = 'info' | 'warning' | 'exception' | 'block';

export interface Finding {
  code: string;
  severity: Severity;
  message: string;
  invoiceLine: number | null;
  poLine: number | null;
  expected: number | null;
  actual: number | null;
  variancePct: number | null;
  amountImpact: number | null;
}

export interface MatchReport {
  supplierId: string | null;
  supplierName: string | null;
  poNumber: string | null;
  currency: string | null;
  invoiceTotal: number | null;
  expectedTotal: number | null;
  findings: Finding[];
}

export interface Decision {
  outcome: Outcome;
  source: 'agent' | 'policyOverride' | 'policyOnly';
  reason: string;
  approver: { role: string; email: string } | null;
  supplierEmail: { to: string; subject: string; body: string } | null;
  policy: { required: Outcome; rules: { findingCode: string; outcome: Outcome; why: string }[] };
  agentOutcome: Outcome | null;
}

export interface HumanAction {
  actor: string;
  action: string;
  outcome: Outcome | null;
  note: string | null;
  at: string;
}

export interface OutboxEmail {
  to: string;
  subject: string;
  body: string;
  queuedAt: string;
  sentAt: string | null;
}

export interface AuditEvent {
  invoiceId: string;
  seq: number;
  at: string;
  actor: string;
  type: string;
  summary: string;
  data: Record<string, string>;
  prevHash: string;
  hash: string;
}

export interface AuditTrail {
  verification: { intact: boolean; brokenAtSeq: number | null; count: number };
  events: AuditEvent[];
}

export interface ReviewRequest {
  action: 'confirm' | 'override' | 'approve' | 'reject';
  expectedVersion: number;
  outcome?: Outcome;
  note?: string;
}

/** Statuses where the pipeline is still working, so the page keeps refreshing. */
export const PROCESSING: ReadonlySet<InvoiceStatus> = new Set<InvoiceStatus>(['received', 'extracting', 'extracted', 'investigating']);

export const STATUS_LABELS: Record<InvoiceStatus, string> = {
  received: 'Received',
  extracting: 'Reading…',
  extracted: 'Read',
  extractionFailed: 'Could not read',
  investigating: 'Investigating…',
  awaitingReview: 'Awaiting review',
  investigationFailed: 'Agent failed',
  awaitingApproval: 'Awaiting approver',
  approved: 'Approved',
  onHold: 'On hold',
  rejected: 'Rejected',
};

export const OUTCOME_LABELS: Record<Outcome, string> = {
  approve: 'Approve',
  routeForApproval: 'Route for approval',
  hold: 'Hold',
  reject: 'Reject',
};

@Injectable({ providedIn: 'root' })
export class InvoicesApi {
  private readonly http = inject(HttpClient);

  list(): Observable<Invoice[]> {
    return this.http.get<Invoice[]>('/api/invoices');
  }

  get(id: string): Observable<Invoice> {
    return this.http.get<Invoice>(`/api/invoices/${encodeURIComponent(id)}`);
  }

  upload(file: File): Observable<Invoice> {
    const form = new FormData();
    form.append('file', file, file.name);
    return this.http.post<Invoice>('/api/invoices', form);
  }

  audit(id: string): Observable<AuditTrail> {
    return this.http.get<AuditTrail>(`/api/invoices/${encodeURIComponent(id)}/audit`);
  }

  act(id: string, request: ReviewRequest): Observable<Invoice> {
    return this.http.post<Invoice>(`/api/invoices/${encodeURIComponent(id)}/actions`, request);
  }

  investigate(id: string): Observable<Invoice> {
    return this.http.post<Invoice>(`/api/invoices/${encodeURIComponent(id)}/investigate`, null);
  }

  /** Reads the invoice again (after a failure, or once AI is configured). */
  extract(id: string): Observable<Invoice> {
    return this.http.post<Invoice>(`/api/invoices/${encodeURIComponent(id)}/extract`, null);
  }

  fileUrl(id: string): string {
    return `/api/invoices/${encodeURIComponent(id)}/file`;
  }
}

/** Turns an API error (RFC 7807 problem details) into one readable sentence. */
export function problemMessage(err: unknown, fallback: string): string {
  if (err instanceof HttpErrorResponse) {
    const body = err.error as { detail?: string; errors?: Record<string, string[]> } | null;
    const fieldError = body?.errors ? Object.values(body.errors).flat()[0] : undefined;
    if (body?.detail) return body.detail;
    if (fieldError) return fieldError;
    if (err.status === 0) return 'Cannot reach the server.';
  }
  return fallback;
}
