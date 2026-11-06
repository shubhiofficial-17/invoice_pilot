import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { AppComponent } from './app.component';

describe('AppComponent', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [AppComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  function statusText(fixture: { nativeElement: HTMLElement }): string {
    return fixture.nativeElement.querySelector('[data-testid=api-status]')?.textContent?.trim() ?? '';
  }

  it('shows "checking" until the health call returns', () => {
    const fixture = TestBed.createComponent(AppComponent);
    fixture.detectChanges();
    expect(statusText(fixture)).toContain('Checking');
    http.expectOne('/api/health').flush({ status: 'ok', service: 'invoicepilot-api' });
  });

  it('shows the API as online when health is ok', () => {
    const fixture = TestBed.createComponent(AppComponent);
    http.expectOne('/api/health').flush({ status: 'ok', service: 'invoicepilot-api' });
    fixture.detectChanges();
    expect(statusText(fixture)).toContain('API online');
  });

  it('shows the API as offline when the call fails', () => {
    const fixture = TestBed.createComponent(AppComponent);
    http.expectOne('/api/health').flush('down', { status: 503, statusText: 'Service Unavailable' });
    fixture.detectChanges();
    expect(statusText(fixture)).toContain('API offline');
  });
});
