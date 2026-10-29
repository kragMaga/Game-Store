// Tokens and recent-auth proofs live in memory only. A reload requires sign-in;
// no passwords, access tokens or refresh tokens are written to browser storage.
type Tokens = { accessToken: string; refreshToken?: string; expiresIn: number };
export type LoginResult = Tokens | { requiresTwoFactor: true; challengeToken: string };
let accessToken: string | undefined;
let refreshToken: string | undefined;
let csrf: string | undefined;
let proof: string | undefined;
let refreshFlight: Promise<void> | undefined;
let generation = 0;
let expired: () => void = () => {};
export function onSessionExpired(handler: () => void) {
  expired = handler;
}
export function clearSession() {
  generation++;
  accessToken = refreshToken = csrf = proof = undefined;
  refreshFlight = undefined;
}
export function acceptTokens(tokens: Tokens) {
  accessToken = tokens.accessToken;
  refreshToken = tokens.refreshToken;
}
export function setProof(value: string) {
  proof = value;
}
export class ApiError extends Error {
  constructor(
    public status: number,
    public title: string,
    message: string,
    public retryAfter?: string,
  ) {
    super(message);
  }
  get needsReauthentication() {
    return this.status === 403 && this.title === 'Recent authentication required';
  }
}
async function error(response: Response): Promise<ApiError> {
  const body = await response.json().catch(() => ({}));
  const validation = body.errors ? Object.values(body.errors).flat().join(' ') : '';
  const title = body.title || 'Request failed';
  if (title === 'Recent authentication required')
    return new ApiError(response.status, title, 'Confirm your identity, then retry this change.');
  if (response.status >= 500)
    return new ApiError(
      response.status,
      title,
      'The service could not complete this request. Please try again later.',
    );
  return new ApiError(
    response.status,
    title,
    validation || body.detail || title,
    response.headers.get('Retry-After') || undefined,
  );
}
// Network failures need a useful message; cancellation should remain a silent
// query cancellation instead of becoming a connection error banner.
async function request(path: string, options: RequestInit): Promise<Response> {
  try {
    return await fetch(path, options);
  } catch (failure) {
    if (failure instanceof Error && failure.name === 'AbortError') throw failure;
    throw new ApiError(
      0,
      'Connection unavailable',
      'Unable to reach the service. Check your connection and try again.',
    );
  }
}
async function rotate() {
  // Only one refresh may consume the rotating token, even when several queries
  // expire together. A response from a logged-out session must never restore it.
  const version = generation;
  const response = await request('/api/auth/refresh', {
    method: 'POST',
    credentials: 'same-origin',
    headers: { 'Content-Type': 'application/json', ...(csrf ? { 'X-CSRF-TOKEN': csrf } : {}) },
    body: JSON.stringify(refreshToken ? { refreshToken } : {}),
  });
  if (!response.ok) throw await error(response);
  const tokens: Tokens = await response.json();
  if (version !== generation) throw new Error('Session changed');
  csrf = response.headers.get('X-CSRF-TOKEN') || csrf;
  acceptTokens(tokens);
}
export async function api<T>(path: string, options: RequestInit = {}, retry = true): Promise<T> {
  const version = generation;
  const headers = new Headers(options.headers);
  if (options.body) headers.set('Content-Type', 'application/json');
  if (accessToken) headers.set('Authorization', `Bearer ${accessToken}`);
  if (proof) headers.set('X-Reauthentication-Token', proof);
  if (csrf) headers.set('X-CSRF-TOKEN', csrf);
  const response = await request(`/api${path}`, {
    ...options,
    headers,
    credentials: 'same-origin',
  });
  if (version !== generation)
    throw new Error('Session changed. Please retry in your current workspace.');
  if (response.status === 401 && retry && accessToken && !path.startsWith('/auth/')) {
    try {
      if (!refreshFlight) {
        const flight = rotate();
        refreshFlight = flight;
        void flight
          .finally(() => {
            if (refreshFlight === flight) refreshFlight = undefined;
          })
          .catch(() => {});
      }
      await refreshFlight;
      if (version !== generation) throw new Error('Session changed');
    } catch {
      if (version === generation) {
        clearSession();
        expired();
      }
      throw new ApiError(401, 'Session expired', 'Your session has expired. Please sign in again.');
    }
    return api<T>(path, options, false);
  }
  if (!response.ok) {
    // Rejected ordinary reads invalidate the session; failed password/MFA checks
    // should instead stay on their current form and show the server's error.
    if (response.status === 401 && !path.startsWith('/auth/')) {
      clearSession();
      expired();
    }
    throw await error(response);
  }
  const nextCsrf = response.headers.get('X-CSRF-TOKEN');
  if (nextCsrf) csrf = nextCsrf;
  if (response.status === 204) return undefined as T;
  return response.json() as Promise<T>;
}
export const post = <T>(path: string, body: unknown) =>
  api<T>(path, { method: 'POST', body: JSON.stringify(body) });
export const put = <T>(path: string, body: unknown) =>
  api<T>(path, { method: 'PUT', body: JSON.stringify(body) });
export async function logout() {
  try {
    await post('/auth/logout', refreshToken ? { refreshToken } : {});
  } finally {
    clearSession();
  }
}
