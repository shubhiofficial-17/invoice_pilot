import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Observable } from 'rxjs';

export type InvoiceStatus = 'received' | 'extracting' | 'extracted' | 'extractionFailed';

export interface InvoiceFile {
  fileName: string;
  contentType: string;
  size: number;
  sha256: string;
}

export interface Invoice {
  id: string;
  status: InvoiceStatus;
  file: InvoiceFile;
  receivedAt: string;
  updatedAt: string;
  version: number;
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
