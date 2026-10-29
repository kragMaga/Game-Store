import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, expect, it, vi } from 'vitest';
import Auth from '../pages/Auth';
import Products from '../pages/Products';
import { SessionProvider } from '../session';
import { clearSession } from '../api';

// Product tests use a selected tenant but still exercise real API-client requests,
// dropdown loading and submitted foreign-key IDs through the rendered form.
vi.mock('../App', () => ({
  useBusiness: () => ({ id: 'business-one', name: 'Test store', currency: 'NGN', role: 'Owner' }),
}));
function renderPage(page: React.ReactNode) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter>
        <SessionProvider>{page}</SessionProvider>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}
afterEach(() => clearSession());
it('performs MFA sign-in as a separate challenge without resubmitting the password', async () => {
  const fetcher = vi
    .fn()
    .mockResolvedValueOnce(
      Response.json({ requiresTwoFactor: true, challengeToken: 'challenge' }, { status: 202 }),
    )
    .mockResolvedValueOnce(
      Response.json({ accessToken: 'access', refreshToken: 'refresh', expiresIn: 600 }),
    );
  vi.stubGlobal('fetch', fetcher);
  renderPage(<Auth />);
  fireEvent.change(screen.getByLabelText('Username'), { target: { value: 'owner' } });
  fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'Secret-River-123!' } });
  fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
  const code = await screen.findByLabelText('Verification code');
  expect(screen.queryByLabelText('Password')).not.toBeInTheDocument();
  fireEvent.change(code, { target: { value: '123456' } });
  fireEvent.click(screen.getByRole('button', { name: 'Verify and sign in' }));
  await waitFor(() => expect(fetcher).toHaveBeenCalledTimes(2));
  expect(JSON.parse(fetcher.mock.calls[1][1].body)).toEqual({
    challengeToken: 'challenge',
    code: '123456',
  });
});
it('creates a product using tenant-owned category and unit IDs, not a free-text stock label', async () => {
  let saved: Record<string, unknown> | undefined;
  const fetcher = vi.fn(async (url: string, init: RequestInit) => {
    if (init.method === 'POST') {
      saved = JSON.parse(String(init.body));
      return Response.json({ id: 'product' });
    }
    const items = url.includes('/categories')
      ? [
          { id: 'category-one', name: 'Beverages', isActive: true },
          { id: 'inactive-category', name: 'Retired', isActive: false },
        ]
      : url.includes('/units-of-measure')
        ? [{ id: 'unit-one', name: 'Piece', isActive: true }]
        : [];
    return Response.json({
      items,
      totalItems: items.length,
      totalPages: 1,
      page: 1,
      pageSize: 100,
    });
  });
  vi.stubGlobal('fetch', fetcher);
  renderPage(<Products />);
  fireEvent.click(screen.getByRole('button', { name: 'Add product' }));
  await screen.findByRole('option', { name: 'Piece' });
  expect(screen.queryByRole('option', { name: 'Retired' })).not.toBeInTheDocument();
  fireEvent.change(screen.getByLabelText('Product name'), { target: { value: 'Bottled water' } });
  fireEvent.change(screen.getByLabelText('SKU'), { target: { value: 'WATER' } });
  fireEvent.change(screen.getByLabelText('Category'), { target: { value: 'category-one' } });
  fireEvent.change(screen.getByLabelText('Base stock unit'), { target: { value: 'unit-one' } });
  fireEvent.click(screen.getByRole('button', { name: 'Save product' }));
  await waitFor(() =>
    expect(saved).toMatchObject({
      categoryId: 'category-one',
      stockUnitId: 'unit-one',
      name: 'Bottled water',
    }),
  );
  expect(saved).not.toHaveProperty('stockUnit');
  expect(
    fetcher.mock.calls
      .filter(([url]) => url.includes('/businesses/'))
      .every(([url]) => url.includes('/businesses/business-one/')),
  ).toBe(true);
});
