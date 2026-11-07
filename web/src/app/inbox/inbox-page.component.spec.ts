import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { InboxPageComponent } from './inbox-page.component';
import { Invoice } from '../invoices/invoices-api';

function invoice(id: string, fileName = `${id}.pdf`): Invoice {
  return {
    id,
    status: 'received',
    file: { fileName, contentType: 'application/pdf', size: 2048, sha256: id },
    receivedAt: '2026-11-06T10:00:00Z',
    updatedAt: '2026-11-06T10:00:00Z',
    version: 1,
    extraction: null,
    failureReason: null,
    agent: null,
  };
}

describe('InboxPageComponent', () => {
  let fixture: ComponentFixture<InboxPageComponent>;
  let http: HttpTestingController;

  const el = (): HTMLElement => fixture.nativeElement;
  const rows = () => el().querySelectorAll('[data-testid=invoice-row]');

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [InboxPageComponent],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(InboxPageComponent);
    fixture.detectChanges();
  });

  afterEach(() => http.verify());

  it('lists invoices from the API', () => {
    http.expectOne('/api/invoices').flush([invoice('ap_1'), invoice('ap_2')]);
    fixture.detectChanges();

    expect(rows().length).toBe(2);
    expect(rows()[0].textContent).toContain('ap_1.pdf');
    expect(rows()[0].querySelector('a')?.getAttribute('href')).toBe('/api/invoices/ap_1/file');
  });

  it('shows what was read from an invoice', () => {
    const read: Invoice = {
      ...invoice('ap_read'),
      status: 'extracted',
      extraction: {
        data: {
          supplierName: 'Acme Industrial Supplies', supplierTaxId: null, invoiceNumber: 'AC/2026/0420',
          invoiceDate: '2026-11-02', dueDate: null, poNumber: 'PO-1001', currency: 'INR',
          lines: [], subtotal: 5700, taxTotal: 1026, total: 6726,
        },
        issues: [{ code: 'total_mismatch', message: 'Totals do not add up.' }],
        source: 'pdf-text', model: 'm', attempts: 1, usage: { promptTokens: 1, completionTokens: 1 }, at: '2026-11-06T10:00:00Z',
      },
    };
    http.expectOne('/api/invoices').flush([read]);
    fixture.detectChanges();

    const text = rows()[0].textContent ?? '';
    expect(text).toContain('Acme Industrial Supplies');
    expect(text).toContain('AC/2026/0420');
    expect(text).toContain('INR 6,726.00');
    expect(el().querySelector('[data-testid=issues]')?.textContent).toContain('1 issue');
  });

  it('retries a failed invoice', () => {
    const failed: Invoice = { ...invoice('ap_bad'), status: 'extractionFailed', failureReason: 'No AI' };
    http.expectOne('/api/invoices').flush([failed]);
    fixture.detectChanges();

    (el().querySelector('[data-testid=retry]') as HTMLButtonElement).click();
    http.expectOne({ method: 'POST', url: '/api/invoices/ap_bad/extract' }).flush({ ...failed, status: 'received', failureReason: null });
    fixture.detectChanges();

    expect(el().querySelector('[data-testid=retry]')).toBeNull();
    expect(rows()[0].textContent).toContain('Received');
  });

  it('shows an empty state', () => {
    http.expectOne('/api/invoices').flush([]);
    fixture.detectChanges();

    expect(el().querySelector('[data-testid=empty]')).not.toBeNull();
  });

  it('uploads a file as form data and puts it at the top', () => {
    http.expectOne('/api/invoices').flush([invoice('ap_old')]);

    fixture.componentInstance.upload(new File(['%PDF-1.4'], 'new.pdf', { type: 'application/pdf' }));
    const req = http.expectOne({ method: 'POST', url: '/api/invoices' });
    expect(req.request.body instanceof FormData).toBeTrue();
    expect((req.request.body as FormData).get('file')).toBeTruthy();
    req.flush(invoice('ap_new', 'new.pdf'));
    fixture.detectChanges();

    expect(rows().length).toBe(2);
    expect(rows()[0].textContent).toContain('new.pdf');
  });

  it('does not show a re-uploaded file twice', () => {
    http.expectOne('/api/invoices').flush([invoice('ap_1')]);

    fixture.componentInstance.upload(new File(['x'], 'again.pdf'));
    http.expectOne({ method: 'POST', url: '/api/invoices' }).flush(invoice('ap_1'));
    fixture.detectChanges();

    expect(rows().length).toBe(1);
  });

  it('shows the server problem detail when an upload is rejected', () => {
    http.expectOne('/api/invoices').flush([]);

    fixture.componentInstance.upload(new File(['hello'], 'notes.txt'));
    http
      .expectOne({ method: 'POST', url: '/api/invoices' })
      .flush({ title: 'Unsupported file type', detail: 'Upload a PDF, PNG or JPEG invoice.' }, { status: 415, statusText: 'Unsupported Media Type' });
    fixture.detectChanges();

    expect(el().querySelector('[data-testid=error]')?.textContent).toContain('Upload a PDF, PNG or JPEG invoice.');
    expect(fixture.componentInstance.uploading()).toBeFalse();
  });

  it('shows a field validation error', () => {
    http.expectOne('/api/invoices').flush([]);

    fixture.componentInstance.upload(new File(['x'], 'big.pdf'));
    http
      .expectOne({ method: 'POST', url: '/api/invoices' })
      .flush({ errors: { file: ['The file is larger than 10 MB.'] } }, { status: 400, statusText: 'Bad Request' });
    fixture.detectChanges();

    expect(el().querySelector('[data-testid=error]')?.textContent).toContain('10 MB');
  });
});
