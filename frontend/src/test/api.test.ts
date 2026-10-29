import { beforeEach, describe, expect, it, vi } from 'vitest';
import {
  acceptTokens,
  api,
  ApiError,
  clearSession,
  onSessionExpired,
  post,
  setProof,
} from '../api';

// These tests cover rotating-token concurrency and session boundaries; a naive
// mock of only successful fetches would miss both classes of authentication bugs.
describe('API session handling', () => {
  beforeEach(() => {
    clearSession();
    onSessionExpired(() => {});
  });
  it('refreshes once for simultaneous expired requests and retries with the new token', async () => {
    acceptTokens({ accessToken: 'old', refreshToken: 'rotating-secret', expiresIn: 1 });
    const fetcher = vi.fn(async (url: string, init: RequestInit) => {
      if (url.endsWith('/auth/refresh')) {
        expect(JSON.parse(String(init.body)).refreshToken).toBe('rotating-secret');
        return Response.json({ accessToken: 'new', refreshToken: 'next-secret', expiresIn: 600 });
      }
      return new Headers(init.headers).get('Authorization') === 'Bearer new'
        ? Response.json({ ok: true })
        : Response.json({ title: 'Expired' }, { status: 401 });
    });
    vi.stubGlobal('fetch', fetcher);
    await Promise.all([api('/businesses/'), api('/businesses/second/settings')]);
    expect(fetcher.mock.calls.filter(([url]) => url.endsWith('/auth/refresh'))).toHaveLength(1);
  });
  it('retains cookie-mode CSRF and attaches the recent-auth proof without browser storage', async () => {
    const storage = vi.spyOn(Storage.prototype, 'setItem');
    const fetcher = vi
      .fn()
      .mockResolvedValueOnce(
        Response.json(
          { accessToken: 'access', expiresIn: 600 },
          { headers: { 'X-CSRF-TOKEN': 'csrf-proof' } },
        ),
      )
      .mockResolvedValueOnce(new Response(null, { status: 204 }));
    vi.stubGlobal('fetch', fetcher);
    const tokens = await post<{ accessToken: string; expiresIn: number }>('/auth/login', {
      username: 'owner',
      password: 'not-stored',
    });
    acceptTokens(tokens);
    setProof('recent-proof');
    await post('/auth/logout', {});
    const headers = new Headers(fetcher.mock.calls[1][1].headers);
    expect(headers.get('X-CSRF-TOKEN')).toBe('csrf-proof');
    expect(headers.get('X-Reauthentication-Token')).toBe('recent-proof');
    expect(storage).not.toHaveBeenCalled();
    storage.mockRestore();
  });
  it('does not restore a session when refresh completes after logout', async () => {
    acceptTokens({ accessToken: 'old', refreshToken: 'old-refresh', expiresIn: 1 });
    let finish!: (response: Response) => void;
    const expired = vi.fn();
    onSessionExpired(expired);
    vi.stubGlobal(
      'fetch',
      vi.fn(async (url: string) =>
        url.endsWith('/auth/refresh')
          ? new Promise<Response>((resolve) => {
              finish = resolve;
            })
          : Response.json({}, { status: 401 }),
      ),
    );
    const request = api('/businesses/');
    await vi.waitFor(() => expect(finish).toBeTypeOf('function'));
    clearSession();
    acceptTokens({ accessToken: 'different-account', expiresIn: 600 });
    finish(Response.json({ accessToken: 'stale-account', expiresIn: 600 }));
    await expect(request).rejects.toBeInstanceOf(ApiError);
    expect(expired).not.toHaveBeenCalled();
  });
  it('does not retry rejected writes or expose API instructions in reauthentication messages', async () => {
    const fetcher = vi
      .fn()
      .mockResolvedValue(
        Response.json(
          { title: 'Recent authentication required', detail: 'Send X-Reauthentication-Token' },
          { status: 403 },
        ),
      );
    vi.stubGlobal('fetch', fetcher);
    await expect(post('/businesses/id/products', {})).rejects.toMatchObject({
      needsReauthentication: true,
      message: 'Confirm your identity, then retry this change.',
    });
    expect(fetcher).toHaveBeenCalledTimes(1);
  });
});
