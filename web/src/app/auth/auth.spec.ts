import { TestBed } from '@angular/core/testing';
import { provideHttpClient, withInterceptors, HttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Router, provideRouter } from '@angular/router';
import { AuthService } from './auth.service';
import { authInterceptor } from './auth.interceptor';
import { safeReturnUrl } from './auth.guard';
import { SignInPageComponent } from './sign-in-page.component';

describe('auth', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [SignInPageComponent],
      providers: [provideHttpClient(withInterceptors([authInterceptor])), provideHttpClientTesting(), provideRouter([])],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('only returns to paths inside the app', () => {
    expect(safeReturnUrl('/invoices/ap_1')).toBe('/invoices/ap_1');
    expect(safeReturnUrl('//evil.example')).toBe('/');
    expect(safeReturnUrl('https://evil.example')).toBe('/');
    expect(safeReturnUrl(undefined)).toBe('/');
  });

  it('asks the server who is signed in only once', () => {
    const auth = TestBed.inject(AuthService);
    let first: unknown;
    auth.ensureLoaded().subscribe((u) => (first = u));
    http.expectOne('/api/account/me').flush({ email: 'asha.rao@invoicepilot.example', name: 'Asha', role: 'clerk' });

    auth.ensureLoaded().subscribe();
    http.expectNone('/api/account/me');
    expect((first as { role: string }).role).toBe('clerk');
    expect(auth.me()?.email).toBe('asha.rao@invoicepilot.example');
  });

  it('treats a 401 from the api as the end of the session', () => {
    const auth = TestBed.inject(AuthService);
    const router = TestBed.inject(Router);
    const navigate = spyOn(router, 'navigate').and.resolveTo(true);
    auth.me.set({ email: 'a', name: 'A', role: 'clerk' });

    TestBed.inject(HttpClient).get('/api/invoices').subscribe({ error: () => undefined });
    http.expectOne('/api/invoices').flush(null, { status: 401, statusText: 'Unauthorized' });

    expect(auth.me()).toBeNull();
    expect(navigate).toHaveBeenCalledWith(['/sign-in'], jasmine.any(Object));
  });

  it('signs in and shows the server error on failure', () => {
    const fixture = TestBed.createComponent(SignInPageComponent);
    fixture.detectChanges();
    const page = fixture.componentInstance;
    page.email = 'asha.rao@invoicepilot.example';
    page.password = 'nope';

    page.submit();
    http.expectOne('/api/account/signin')
      .flush({ title: 'Sign-in failed', detail: 'The email or password is wrong.' }, { status: 401, statusText: 'Unauthorized' });
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid=error]').textContent).toContain('email or password is wrong');
  });
});
