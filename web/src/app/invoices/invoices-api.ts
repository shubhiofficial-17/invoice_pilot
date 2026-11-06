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
}

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
