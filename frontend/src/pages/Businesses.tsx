import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link, useNavigate } from 'react-router-dom';
import { ArrowRight, Building2, Plus } from 'lucide-react';
import { ApiError, api, post } from '../api';
import { useSession } from '../session';
import type { Business, Page } from '../types';
import { Button, Empty, ErrorNotice, Loading, Modal, Pagination } from '../components/ui';

// Tenant IDs come from the API membership list. Route IDs still undergo server
// authorization; displaying a business card never grants access by itself.
export default function Businesses() {
  const [page, setPage] = useState(1);
  const [create, setCreate] = useState(false);
  const session = useSession();
  const query = useQuery({
    queryKey: ['businesses', page],
    queryFn: ({ signal }) =>
      api<Page<Business>>(`/businesses/?page=${page}&pageSize=20`, { signal }),
  });
  return (
    <div className="business-page">
      <header className="business-header">
        <Link to="/" className="brand">
          <span className="brand-icon">
            <Building2 />
          </span>
          TradeNest
        </Link>
        <Button
          variant="ghost"
          onClick={() => {
            void session.signOut().catch(() => {});
          }}
        >
          Sign out
        </Button>
      </header>
      <main className="business-content">
        <span className="eyebrow">Welcome, {session.username}</span>
        <div className="page-heading">
          <div>
            <h1>Your businesses</h1>
            <p>Choose a workspace or start a new one.</p>
          </div>
          <Button onClick={() => setCreate(true)}>
            <Plus size={17} />
            New business
          </Button>
        </div>
        <ErrorNotice error={query.error} />
        {query.isPending ? (
          <Loading />
        ) : query.data?.items.length === 0 ? (
          <Empty title="Your first workspace starts here">
            Create a business to organize products and track inventory.
          </Empty>
        ) : (
          <div className="business-grid">
            {query.data?.items.map((b) => (
              <Link className="business-card" key={b.id} to={`/businesses/${b.id}/dashboard`}>
                <span className="business-avatar">
                  <Building2 />
                </span>
                <span className="badge">{b.role}</span>
                <h2>{b.name}</h2>
                <p>
                  {b.currency} ·{' '}
                  {b.closedAtUtc ? 'Closed · retained reports only' : 'Active workspace'}
                </p>
                <span className="card-link">
                  Open workspace
                  <ArrowRight size={17} />
                </span>
              </Link>
            ))}
          </div>
        )}
        {query.data && <Pagination page={page} pages={query.data.totalPages} setPage={setPage} />}
      </main>
      {create && <CreateBusiness close={() => setCreate(false)} />}
    </div>
  );
}
function CreateBusiness({ close }: { close: () => void }) {
  const client = useQueryClient();
  const navigate = useNavigate();
  const session = useSession();
  const mutation = useMutation({
    mutationFn: (body: unknown) => post<Business>('/businesses/', body),
    onSuccess: async (b) => {
      await client.invalidateQueries({ queryKey: ['businesses'] });
      close();
      navigate(`/businesses/${b.id}/dashboard`);
    },
  });
  return (
    <Modal title="Create a business" onClose={close}>
      <form
        onSubmit={(event) => {
          event.preventDefault();
          const data = new FormData(event.currentTarget);
          mutation.mutate({
            name: data.get('name'),
            currency: String(data.get('currency')).toUpperCase(),
            notificationEmail: data.get('email'),
            timeZoneId: data.get('timezone'),
            currencyDecimalPlaces: Number(data.get('precision')),
          });
        }}
      >
        <label>
          Business name
          <input name="name" required minLength={2} maxLength={120} />
        </label>
        <div className="form-grid">
          <label>
            Currency code
            <input name="currency" placeholder="NGN" required pattern="[A-Za-z]{3}" maxLength={3} />
          </label>
          <label>
            Decimal places
            <select name="precision" defaultValue="2">
              {[0, 1, 2, 3, 4].map((n) => (
                <option key={n}>{n}</option>
              ))}
            </select>
          </label>
        </div>
        <small className="muted">Currency and precision are fixed after creation.</small>
        <label>
          Business notification email
          <input type="email" name="email" required maxLength={254} />
        </label>
        <label>
          Time zone
          <input
            name="timezone"
            defaultValue={Intl.DateTimeFormat().resolvedOptions().timeZone || 'Africa/Lagos'}
            required
            maxLength={100}
          />
        </label>
        <ErrorNotice error={mutation.error} />
        {mutation.error instanceof ApiError && mutation.error.needsReauthentication && (
          <Button type="button" variant="secondary" onClick={session.reauthenticate}>
            Confirm identity
          </Button>
        )}
        <Button busy={mutation.isPending}>Create workspace</Button>
      </form>
    </Modal>
  );
}
