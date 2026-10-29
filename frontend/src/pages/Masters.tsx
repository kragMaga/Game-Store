import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Plus } from 'lucide-react';
import { ApiError, api, post, put } from '../api';
import { useBusiness } from '../App';
import { tenantKey } from '../queries';
import { useSession } from '../session';
import { Button, Empty, ErrorNotice, Loading, Modal, Pagination } from '../components/ui';
import { isAdmin, type Master, type Page } from '../types';

// Deactivate instead of delete: products and posted history must retain valid
// references. Inactive rows stay visible here so administrators can restore them.
export default function Masters() {
  const b = useBusiness();
  const [kind, setKind] = useState<'categories' | 'units-of-measure'>('categories');
  const [page, setPage] = useState(1);
  const [edit, setEdit] = useState<Master | 'new'>();
  const query = useQuery({
    queryKey: [...tenantKey(b.id), kind, page],
    queryFn: ({ signal }) =>
      api<Page<Master>>(`/businesses/${b.id}/${kind}?page=${page}&pageSize=20`, { signal }),
  });
  return (
    <>
      <div className="page-heading">
        <div>
          <span className="eyebrow">Built around your business</span>
          <h1>Master data</h1>
          <p>Manage the lists your inventory uses every day.</p>
        </div>
        {isAdmin(b) && (
          <Button onClick={() => setEdit('new')}>
            <Plus size={17} />
            Add {kind === 'categories' ? 'category' : 'unit'}
          </Button>
        )}
      </div>
      <div className="tabs" aria-label="Master data lists">
        {(['categories', 'units-of-measure'] as const).map((k) => (
          <button
            key={k}
            aria-pressed={kind === k}
            className={kind === k ? 'active' : ''}
            onClick={() => {
              setKind(k);
              setPage(1);
            }}
          >
            {k === 'categories' ? 'Categories' : 'Units of measure'}
          </button>
        ))}
      </div>
      <section className="panel">
        <div className="panel-heading">
          <div>
            <h2>{kind === 'categories' ? 'Product categories' : 'Units of measure'}</h2>
            <p>
              {kind === 'categories'
                ? 'Organize products and report by category.'
                : 'Reusable unit names; pack conversions remain specific to each product.'}
            </p>
          </div>
        </div>
        <ErrorNotice error={query.error} />
        {query.error && (
          <Button variant="secondary" onClick={() => void query.refetch()}>
            Try again
          </Button>
        )}
        {query.isPending ? (
          <Loading />
        ) : query.data?.items.length === 0 ? (
          <Empty
            title={
              kind === 'categories' ? 'Make room for your categories' : 'Define how you count stock'
            }
          >
            {kind === 'categories'
              ? 'Add categories such as Beverages, Clothing, or Electronics.'
              : 'Add units such as Piece, Kilogram, Litre, or Carton.'}
          </Empty>
        ) : (
          <div className="table-scroll">
            <table>
              <thead>
                <tr>
                  <th>Name</th>
                  <th>Status</th>
                  {isAdmin(b) && <th>Actions</th>}
                </tr>
              </thead>
              <tbody>
                {query.data?.items.map((row) => (
                  <tr key={row.id}>
                    <td>
                      <strong>{row.name}</strong>
                    </td>
                    <td>
                      <span className={`badge ${row.isActive ? 'positive' : ''}`}>
                        {row.isActive ? 'Active' : 'Inactive'}
                      </span>
                    </td>
                    {isAdmin(b) && (
                      <td>
                        <Button variant="ghost" onClick={() => setEdit(row)}>
                          Edit
                        </Button>
                      </td>
                    )}
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
        {query.data && <Pagination page={page} pages={query.data.totalPages} setPage={setPage} />}
      </section>
      {edit && (
        <MasterForm
          kind={kind}
          row={edit === 'new' ? undefined : edit}
          close={() => setEdit(undefined)}
        />
      )}
    </>
  );
}
function MasterForm({
  kind,
  row,
  close,
}: {
  kind: 'categories' | 'units-of-measure';
  row?: Master;
  close: () => void;
}) {
  const b = useBusiness();
  const client = useQueryClient();
  const session = useSession();
  const label = kind === 'categories' ? 'category' : 'unit';
  const save = useMutation({
    mutationFn: (body: unknown) =>
      row
        ? put(`/businesses/${b.id}/${kind}/${row.id}`, body)
        : post(`/businesses/${b.id}/${kind}`, body),
    onSuccess: async () => {
      await client.invalidateQueries({ queryKey: tenantKey(b.id) });
      close();
    },
  });
  return (
    <Modal title={`${row ? 'Edit' : 'Add'} ${label}`} onClose={close}>
      <form
        onSubmit={(event) => {
          event.preventDefault();
          const data = new FormData(event.currentTarget);
          save.mutate({
            name: data.get('name'),
            ...(row ? { isActive: data.get('active') === 'on' } : {}),
          });
        }}
      >
        <label>
          Name
          <input
            name="name"
            defaultValue={row?.name}
            required
            minLength={kind === 'categories' ? 2 : 1}
            maxLength={kind === 'categories' ? 100 : 40}
          />
        </label>
        {row && (
          <>
            <label className="checkbox">
              <input type="checkbox" name="active" defaultChecked={row.isActive} />
              Available for new product assignments
            </label>
            <p className="muted">
              Deactivation preserves existing products and history. It does not delete this entry.
            </p>
          </>
        )}
        <ErrorNotice error={save.error} />
        {save.error instanceof ApiError && save.error.needsReauthentication && (
          <Button variant="secondary" type="button" onClick={session.reauthenticate}>
            Confirm identity
          </Button>
        )}
        <Button busy={save.isPending}>Save {label}</Button>
      </form>
    </Modal>
  );
}
