import { api } from './api';
import type { Page } from './types';

// Dropdowns must not silently omit entries beyond page one. Fetch all bounded
// pages, passing cancellation through when a user leaves or switches businesses.
export async function allPages<T>(path: string, signal?: AbortSignal): Promise<T[]> {
  const rows: T[] = [];
  for (let page = 1; page <= 100; page++) {
    const result = await api<Page<T>>(
      `${path}${path.includes('?') ? '&' : '?'}page=${page}&pageSize=100`,
      { signal },
    );
    rows.push(...result.items);
    if (page >= result.totalPages) return rows;
  }
  throw new Error('This list is too large to load. Please contact your administrator.');
}
export const tenantKey = (id: string) => ['tenant', id] as const;
