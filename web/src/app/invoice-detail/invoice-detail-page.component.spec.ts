import { ComponentFixture, TestBed, fakeAsync, tick } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { InvoiceDetailPageComponent } from './invoice-detail-page.component';
import { AuditTrail, Invoice } from '../invoices/invoices-api';

function heldByPolicy(overrides: Partial<Invoice> = {}): Invoice {
  return {
    id: 'ap_1',
    status: 'awaitingReview',
    file: { fileName: 'gloves.pdf', contentType: 'application/pdf', size: 1, sha256: 'x' },
    receivedAt: '2026-11-06T10:00:00Z',
    updatedAt: '2026-11-06T10:01:00Z',
    version: 6,
    failureReason: null,
    extraction: {
      data: {
        supplierName: 'Acme Industrial Supplies', supplierTaxId: null, invoiceNumber: 'AC/2026/0777',
        invoiceDate: '2026-11-02', dueDate: null, poNumber: 'PO-1005', currency: 'INR',
        lines: [{ description: 'Nitrile gloves', sku: 'GLOVE-NIT', quantity: 500, unitPrice: 85, taxRate: 0.12, amount: 42500 }],
        subtotal: 42500, taxTotal: 5100, total: 47600,
      },
      issues: [], source: 'pdf-text', model: 'gpt-x', attempts: 1, usage: { promptTokens: 1, completionTokens: 1 }, at: '2026-11-06T10:00:30Z',
    },
    agent: {
      model: 'gpt-x', status: 'completed', failureReason: null, modelCalls: 4,
      usage: { promptTokens: 3000, completionTokens: 200 }, startedAt: '', finishedAt: '',
      proposal: { outcome: 'approve', reason: 'Looks fine', findingCodes: [], supplierEmail: null },
      steps: [
        { index: 1, kind: 'model', toolName: null, arguments: null, output: '', isError: false, promptTokens: 1, completionTokens: 1, durationMs: 5, at: '' },
        { index: 2, kind: 'tool', toolName: 'run_three_way_match', arguments: '{}', output: '{"findings":[]}', isError: false, promptTokens: 0, completionTokens: 0, durationMs: 3, at: '' },
      ],
    },
    match: {
      supplierId: 'SUP-ACME', supplierName: 'Acme Industrial Supplies', poNumber: 'PO-1005', currency: 'INR',
      invoiceTotal: 47600, expectedTotal: 47600,
      findings: [{ code: 'quantity_exceeds_received', severity: 'exception', message: 'PO line 1: 500 billed, but only 300 received.',
        invoiceLine: null, poLine: 1, expected: 300, actual: 500, variancePct: null, amountImpact: 17000 }],
    },
    decision: {
      outcome: 'hold', source: 'policyOverride', reason: 'The agent proposed approve, but the policy requires hold.',
      approver: null, agentOutcome: 'approve',
      supplierEmail: { to: 'ar@acme-industrial.example', subject: 'Invoice on hold: PO PO-1005', body: 'Dear Acme…' },
      policy: { required: 'hold', rules: [{ findingCode: 'quantity_exceeds_received', outcome: 'hold', why: 'Fix it.' }] },
    },
    actions: [],
    outbox: null,
    booking: null,
    ...overrides,
  };
}

const trail: AuditTrail = {
  verification: { intact: true, brokenAtSeq: null, count: 2 },
  events: [
    { invoiceId: 'ap_1', seq: 1, at: '2026-11-06T10:00:00Z', actor: 'clerk', type: 'invoice.received', summary: 'Uploaded gloves.pdf', data: {}, prevHash: '0', hash: 'a' },
    { invoiceId: 'ap_1', seq: 2, at: '2026-11-06T10:01:00Z', actor: 'policy', type: 'policy.override', summary: 'Policy changed Approve to Hold', data: {}, prevHash: 'a', hash: 'b' },
  ],
};

describe('InvoiceDetailPageComponent', () => {
  let fixture: ComponentFixture<InvoiceDetailPageComponent>;
  let http: HttpTestingController;
  const el = (): HTMLElement => fixture.nativeElement;
  const q = (id: string) => el().querySelector(`[data-testid=${id}]`);

  function open(invoice: Invoice): void {
    fixture.componentRef.setInput('id', invoice.id);
    fixture.detectChanges();
    http.expectOne(`/api/invoices/${invoice.id}`).flush(invoice);
    http.expectOne(`/api/invoices/${invoice.id}/audit`).flush(trail);
    fixture.detectChanges();
  }

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [InvoiceDetailPageComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    });
    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(InvoiceDetailPageComponent);
  });

  afterEach(() => http.verify());

  it('shows the document, the recommendation, the findings and the audit trail', () => {
    open(heldByPolicy());

    expect(q('pdf')?.getAttribute('src')).toBe('/api/invoices/ap_1/file');
    expect(q('outcome')?.textContent).toContain('Hold');
    expect(q('decision')?.textContent).toContain('Policy overrode the agent');
    expect(el().querySelectorAll('[data-testid=finding]').length).toBe(1);
    expect(q('findings')?.textContent).toContain('only 300 received');
    expect(q('total')?.textContent).toContain('INR 47,600.00');
    expect(q('chain-ok')?.textContent).toContain('chain verified (2)');
    expect(q('audit')?.textContent).toContain('Policy changed Approve to Hold');
  });

  it('confirms with the version the person was looking at', () => {
    open(heldByPolicy());

    (q('confirm') as HTMLButtonElement).click();
    const req = http.expectOne({ method: 'POST', url: '/api/invoices/ap_1/actions' });
    expect(req.request.body).toEqual({ action: 'confirm', expectedVersion: 6 });
    req.flush(heldByPolicy({ status: 'onHold', version: 7, outbox: { to: 'ar@acme-industrial.example', subject: 's', body: 'b', queuedAt: '2026-11-06T10:02:00Z', sentAt: null } }));
    http.expectOne('/api/invoices/ap_1/audit').flush(trail);
    fixture.detectChanges();

    expect(q('status')?.textContent).toContain('On hold');
    expect(q('outbox')?.textContent).toContain('ar@acme-industrial.example');
    expect(q('confirm')).toBeNull();
  });

  it('only offers outcomes at least as strict as the policy, and needs a note', () => {
    open(heldByPolicy());
    (q('toggle-override') as HTMLButtonElement).click();
    fixture.detectChanges();

    const options = Array.from(el().querySelectorAll('[data-testid=override-outcome] option')) as HTMLOptionElement[];
    expect(options.filter((o) => !o.disabled).map((o) => o.value)).toEqual(['hold', 'reject']);
    expect((q('submit-override') as HTMLButtonElement).disabled).toBeTrue();
  });

  it('sends an override with its note', async () => {
    open(heldByPolicy());
    (q('toggle-override') as HTMLButtonElement).click();
    fixture.detectChanges();
    fixture.componentInstance.overrideOutcome = 'reject';
    fixture.componentInstance.note = 'We never ordered gloves from this branch.';
    fixture.detectChanges();

    fixture.componentInstance.submitOverride();
    const req = http.expectOne({ method: 'POST', url: '/api/invoices/ap_1/actions' });
    expect(req.request.body).toEqual({ action: 'override', outcome: 'reject', note: 'We never ordered gloves from this branch.', expectedVersion: 6 });
    req.flush(heldByPolicy({ status: 'rejected', version: 7 }));
    http.expectOne('/api/invoices/ap_1/audit').flush(trail);
  });

  it('shows a conflict from the server', () => {
    open(heldByPolicy());

    (q('confirm') as HTMLButtonElement).click();
    http.expectOne({ method: 'POST', url: '/api/invoices/ap_1/actions' })
      .flush({ title: 'Changed meanwhile', detail: 'Someone else acted on this invoice first.' }, { status: 409, statusText: 'Conflict' });
    fixture.detectChanges();

    expect(q('error')?.textContent).toContain('Someone else acted');
  });

  it('gives an approver approve and reject', () => {
    open(heldByPolicy({
      status: 'awaitingApproval',
      decision: { ...heldByPolicy().decision!, outcome: 'routeForApproval', approver: { role: 'buyer', email: 'priya.nair@invoicepilot.example' } },
    }));

    expect(q('confirm')).toBeNull();
    expect(q('decision')?.textContent).toContain('priya.nair@invoicepilot.example');
    expect((q('reject') as HTMLButtonElement).disabled).toBeTrue();
    (q('approve') as HTMLButtonElement).click();
    expect(http.expectOne({ method: 'POST', url: '/api/invoices/ap_1/actions' }).request.body.action).toBe('approve');
  });

  it('shows the agent trace on request', () => {
    open(heldByPolicy());

    expect(el().querySelectorAll('[data-testid=trace-step]').length).toBe(0);
    (q('toggle-trace') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(el().querySelectorAll('[data-testid=trace-step]').length).toBe(1);
    expect(el().textContent).toContain('run_three_way_match');
  });

  it('keeps refreshing while the invoice is being processed', fakeAsync(() => {
    fixture.componentRef.setInput('id', 'ap_1');
    fixture.detectChanges();
    http.expectOne('/api/invoices/ap_1').flush(heldByPolicy({ status: 'investigating', decision: null, match: null }));
    http.expectOne('/api/invoices/ap_1/audit').flush(trail);

    tick(2000);
    http.expectOne('/api/invoices/ap_1').flush(heldByPolicy());
    http.expectOne('/api/invoices/ap_1/audit').flush(trail);
    tick(2000);
    http.expectNone('/api/invoices/ap_1');
  }));

  it('marks a broken audit chain', () => {
    fixture.componentRef.setInput('id', 'ap_1');
    fixture.detectChanges();
    http.expectOne('/api/invoices/ap_1').flush(heldByPolicy());
    http.expectOne('/api/invoices/ap_1/audit').flush({ ...trail, verification: { intact: false, brokenAtSeq: 2, count: 2 } });
    fixture.detectChanges();

    expect(q('chain-broken')?.textContent).toContain('#2');
  });
});
