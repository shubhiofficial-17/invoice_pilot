import { Injectable, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, catchError, map, of, tap } from 'rxjs';

export interface AppUser {
  email: string;
  name: string;
  role: 'clerk' | 'approver' | 'admin';
}

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private loaded = false;

  /** The signed-in user, or null. */
  readonly me = signal<AppUser | null>(null);

  /** Asks the server once who is signed in (the cookie is HttpOnly, so only the server knows). */
  ensureLoaded(): Observable<AppUser | null> {
    if (this.loaded) return of(this.me());
    return this.http.get<AppUser>('/api/account/me').pipe(
      catchError(() => of(null)),
      tap((user) => {
        this.loaded = true;
        this.me.set(user);
      }),
    );
  }

  signIn(email: string, password: string): Observable<AppUser> {
    return this.http.post<AppUser>('/api/account/signin', { email, password }).pipe(
      tap((user) => {
        this.loaded = true;
        this.me.set(user);
      }),
    );
  }

  signOut(): Observable<void> {
    return this.http.post('/api/account/signout', null).pipe(
      map(() => undefined),
      catchError(() => of(undefined)),
      tap(() => this.me.set(null)),
    );
  }

  /** Called when the API says 401: the session ended. */
  expired(): void {
    this.me.set(null);
  }
}
