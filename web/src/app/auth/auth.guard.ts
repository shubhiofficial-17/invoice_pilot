import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { map } from 'rxjs';
import { AuthService } from './auth.service';

export const authGuard: CanActivateFn = (_route, state) => {
  const auth = inject(AuthService);
  const router = inject(Router);
  return auth.ensureLoaded().pipe(
    map((user) => (user ? true : router.createUrlTree(['/sign-in'], { queryParams: { returnUrl: state.url } }))),
  );
};

/** Only same-app paths: "/x" is fine, "//evil.example" and "https://…" are not (open redirect). */
export function safeReturnUrl(url: string | null | undefined): string {
  return url && url.startsWith('/') && !url.startsWith('//') ? url : '/';
}
