import { useRef, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Plus, Search } from 'lucide-react';
import { ApiError, api, post, put } from '../api';
import { useBusiness } from '../App';
import { allPages, tenantKey } from '../queries';
import { useSession } from '../session';
import { Button, Empty, ErrorNotice, Loading, Modal, Pagination } from '../components/ui';
import {
  isAdmin,
  quantity,
  type Master,
  type Page,
  type Product,
  type ProductUnit,
} from '../types';

// Every cache key includes the business ID, including dropdowns and pack units.
// The route also remounts tenant forms on a switch so unsaved data cannot carry over.
export default function Products() {
  const b = useBusiness();
  const admin = isAdmin(b);
  const [page, setPage] = useState(1);
  const [search, setSearch] = useState('');
  const [low, setLow] = useState(false);
  const [edit, setEdit] = useState<Product | 'new'>();
  const [opening, setOpening] = useState<Product>();
  const [units, setUnits] = useState<Product>();
  const products = useQuery({
    queryKey: [...tenantKey(b.id), 'products', page, search, low],
    queryFn: ({ signal }) =>
      api<Page<Product>>(
        `/businesses/${b.id}/products?page=${page}&pageSize=20&search=${encodeURIComponent(search)}&lowStockOnly=${low}`,
        { signal },
      ),
  });
  const categories = useQuery({
    queryKey: [...tenantKey(b.id), 'categories', 'all'],
    queryFn: ({ signal }) => allPages<Master>(`/businesses/${b.id}/categories`, signal),
  });
  return (
    <>
      <div className="page-heading">
        <div>
          <span className="eyebrow">Your inventory</span>
          <h1>Products</h1>
          <p>Keep your catalogue organized and your stock in view.</p>
        </div>
        {admin && (
          <Button onClick={() => setEdit('new')}>
            <Plus size={17} />
            Add product
          </Button>
        )}
      </div>
      <section className="panel">
        <div className="toolbar">
          <form
            className="search-form"
            onSubmit={(event) => {
              event.preventDefault();
              setSearch(String(new FormData(event.currentTarget).get('search') || '').trim());
              setPage(1);
            }}
          >
            <Search size={18} />
            <input
              name="search"
              aria-label="Search products"
              placeholder="Search name or SKU…"
              maxLength={100}
            />
            <Button variant="secondary">Search</Button>
          </form>
          <label className="checkbox">
            <input
              type="checkbox"
              checked={low}
              onChange={(event) => {
                setLow(event.target.checked);
                setPage(1);
              }}
            />
            Low stock only
          </label>
        </div>
        <ErrorNotice error={products.error || categories.error} />
        {products.error && (
          <Button variant="secondary" onClick={() => void products.refetch()}>
            Try again
          </Button>
        )}
        {products.isPending ? (
          <Loading />
        ) : products.data?.items.length === 0 ? (
          <Empty title={search || low ? 'No matching products' : 'Build your product list'}>
            {search || low
              ? 'Try another search or remove the stock filter.'
              : 'Add categories and units in Master data, then create your first product.'}
          </Empty>
        ) : (
          <div className="table-scroll">
            <table>
              <thead>
                <tr>
                  <th>Product</th>
                  <th>Category</th>
                  <th>On hand</th>
                  <th>Reorder at</th>
                  <th>Status</th>
                  <th>Actions</th>
                </tr>
              </thead>
              <tbody>
                {products.data?.items.map((p) => (
                  <tr key={p.id}>
                    <td>
                      <strong>{p.name}</strong>
                      <small>{p.sku}</small>
                    </td>
                    <td>
                      {categories.data?.find((c) => c.id === p.categoryId)?.name ||
                        (p.categoryId ? 'Loading category…' : 'Uncategorized')}
                    </td>
                    <td className={p.isLowStock && p.isActive ? 'text-warning' : ''}>
                      {quantity(p.quantityOnHand)}
                      <small>{p.stockUnit}</small>
                    </td>
                    <td>{quantity(p.reorderLevel)}</td>
                    <td>
                      <span
                        className={`badge ${!p.isActive ? '' : p.isLowStock ? 'warning' : 'positive'}`}
                      >
                        {!p.isActive ? 'Inactive' : p.isLowStock ? 'Low stock' : 'In stock'}
                      </span>
                    </td>
                    <td>
                      <div className="row-actions">
                        <Button variant="ghost" onClick={() => setUnits(p)}>
                          Units
                        </Button>
                        {admin && (
                          <>
                            <Button variant="ghost" onClick={() => setEdit(p)}>
                              Edit
                            </Button>
                            {p.isActive && !p.openingStockRecorded && p.quantityOnHand === 0 && (
                              <Button variant="ghost" onClick={() => setOpening(p)}>
                                Opening stock
                              </Button>
                            )}
                          </>
                        )}
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
        {products.data && (
          <Pagination page={page} pages={products.data.totalPages} setPage={setPage} />
        )}
      </section>
      {edit && (
        <ProductForm product={edit === 'new' ? undefined : edit} close={() => setEdit(undefined)} />
      )}
      {opening && <OpeningStock product={opening} close={() => setOpening(undefined)} />}
      {units && <Units product={units} close={() => setUnits(undefined)} />}
    </>
  );
}
function useMasters(kind: 'categories' | 'units-of-measure') {
  const b = useBusiness();
  return useQuery({
    queryKey: [...tenantKey(b.id), kind, 'all'],
    queryFn: ({ signal }) => allPages<Master>(`/businesses/${b.id}/${kind}`, signal),
  });
}
function SensitiveError({ error }: { error: unknown }) {
  const session = useSession();
  return (
    <>
      <ErrorNotice error={error} />
      {error instanceof ApiError && error.needsReauthentication && (
        <Button type="button" variant="secondary" onClick={session.reauthenticate}>
          Confirm identity
        </Button>
      )}
    </>
  );
}
function ProductForm({ product, close }: { product?: Product; close: () => void }) {
  const b = useBusiness();
  const client = useQueryClient();
  const categories = useMasters('categories');
  const units = useMasters('units-of-measure');
  const save = useMutation({
    mutationFn: (body: unknown) =>
      product
        ? put(`/businesses/${b.id}/products/${product.id}`, body)
        : post(`/businesses/${b.id}/products`, body),
    onSuccess: async () => {
      await client.invalidateQueries({ queryKey: tenantKey(b.id) });
      close();
    },
  });
  return (
    <Modal title={product ? 'Edit product' : 'Add a product'} onClose={close}>
      <form
        onSubmit={(event) => {
          event.preventDefault();
          const data = new FormData(event.currentTarget);
          const common = {
            name: data.get('name'),
            reorderLevel: Number(data.get('reorder')),
            categoryId: data.get('category') || null,
            defaultSellingPrice: data.get('price') === '' ? null : Number(data.get('price')),
          };
          save.mutate(
            product
              ? { ...common, isActive: data.get('active') === 'on' }
              : {
                  ...common,
                  sku: data.get('sku'),
                  stockUnitId: data.get('unit'),
                  allowsFractionalStock: data.get('fractional') === 'on',
                },
          );
        }}
      >
        <label>
          Product name
          <input name="name" defaultValue={product?.name} required minLength={2} maxLength={160} />
        </label>
        <label>
          SKU
          <input
            name="sku"
            defaultValue={product?.sku}
            required
            pattern="[A-Za-z0-9._-]{1,64}"
            maxLength={64}
            disabled={!!product}
          />
        </label>
        <label>
          Category
          <select name="category" defaultValue={product?.categoryId || ''}>
            <option value="">Uncategorized</option>
            {categories.data
              ?.filter((c) => c.isActive || c.id === product?.categoryId)
              .map((c) => (
                <option value={c.id} key={c.id}>
                  {c.name}
                  {!c.isActive && ' (inactive)'}
                </option>
              ))}
          </select>
        </label>
        {product ? (
          <p className="notice">
            Base stock unit: <strong>{product.stockUnit}</strong>. Its meaning and conversion cannot
            be changed.
          </p>
        ) : (
          <>
            <label>
              Base stock unit
              <select name="unit" required defaultValue="">
                <option value="" disabled>
                  Select a unit
                </option>
                {units.data
                  ?.filter((u) => u.isActive)
                  .map((u) => (
                    <option value={u.id} key={u.id}>
                      {u.name}
                    </option>
                  ))}
              </select>
            </label>
            {units.data && !units.data.some((u) => u.isActive) && (
              <p className="notice">Create a unit in Master data before adding a product.</p>
            )}
            <label className="checkbox">
              <input type="checkbox" name="fractional" />
              Allow fractional stock quantities
            </label>
          </>
        )}
        <div className="form-grid">
          <label>
            Reorder quantity
            <input
              name="reorder"
              type="number"
              min="0"
              step="0.000001"
              required
              defaultValue={product?.reorderLevel ?? 0}
            />
          </label>
          <label>
            Suggested selling price
            <input
              name="price"
              type="number"
              min="0"
              step="0.000001"
              defaultValue={product?.defaultSellingPrice ?? ''}
              placeholder="Optional"
            />
          </label>
        </div>
        <small className="muted">
          Price is per base stock unit, in {b.currency}. Actual sale prices can differ.
        </small>
        {product && (
          <label className="checkbox">
            <input type="checkbox" name="active" defaultChecked={product.isActive} />
            Product is active
          </label>
        )}
        <ErrorNotice error={categories.error || units.error} />
        <SensitiveError error={save.error} />
        <Button
          busy={save.isPending}
          disabled={
            categories.isPending ||
            units.isPending ||
            categories.isError ||
            units.isError ||
            (!product && !units.data?.some((u) => u.isActive))
          }
        >
          Save product
        </Button>
      </form>
    </Modal>
  );
}
function OpeningStock({ product, close }: { product: Product; close: () => void }) {
  const b = useBusiness();
  const client = useQueryClient();
  const units = useQuery({
    queryKey: [...tenantKey(b.id), 'units', product.id],
    queryFn: ({ signal }) =>
      allPages<ProductUnit>(`/businesses/${b.id}/products/${product.id}/units`, signal),
  });
  const attempt = useRef<{ json: string; key: string } | undefined>(undefined);
  const save = useMutation({
    mutationFn: (body: unknown) =>
      post(`/businesses/${b.id}/products/${product.id}/opening-stock`, body),
    onSuccess: async () => {
      await client.invalidateQueries({ queryKey: tenantKey(b.id) });
      close();
    },
  });
  return (
    <Modal title={`Opening stock · ${product.name}`} onClose={close}>
      <p className="muted">
        Record stock already held when onboarding. This can be posted only once, before other stock
        movements.
      </p>
      <form
        onSubmit={(event) => {
          event.preventDefault();
          const data = new FormData(event.currentTarget);
          const body = {
            unitId: data.get('unit'),
            quantity: Number(data.get('quantity')),
            effectiveDate: data.get('date'),
            reason: data.get('reason'),
          };
          // An uncertain network response can be retried without posting stock twice.
          // Editing the request starts a new attempt; an unchanged retry reuses its key.
          const json = JSON.stringify(body);
          if (attempt.current?.json !== json) attempt.current = { json, key: crypto.randomUUID() };
          save.mutate({ ...body, idempotencyKey: attempt.current.key });
        }}
      >
        <label>
          Unit
          <select name="unit" required defaultValue="">
            <option value="" disabled>
              Select a product unit
            </option>
            {units.data?.map((u) => (
              <option key={u.id} value={u.id}>
                {u.name} · {quantity(u.conversionFactor)} {product.stockUnit}
              </option>
            ))}
          </select>
        </label>
        <label>
          Quantity
          <input name="quantity" type="number" min="0" step="0.000001" required />
        </label>
        <label>
          Effective date
          <input type="date" name="date" required />
        </label>
        <label>
          Reason
          <textarea
            name="reason"
            required
            minLength={3}
            maxLength={300}
            defaultValue="Stock held at onboarding"
          />
        </label>
        <ErrorNotice error={units.error} />
        <SensitiveError error={save.error} />
        <Button busy={save.isPending} disabled={!units.data}>
          Record opening stock
        </Button>
      </form>
    </Modal>
  );
}
function Units({ product, close }: { product: Product; close: () => void }) {
  const b = useBusiness();
  const client = useQueryClient();
  const measures = useMasters('units-of-measure');
  const units = useQuery({
    queryKey: [...tenantKey(b.id), 'units', product.id],
    queryFn: ({ signal }) =>
      allPages<ProductUnit>(`/businesses/${b.id}/products/${product.id}/units`, signal),
  });
  const save = useMutation({
    mutationFn: (body: unknown) => post(`/businesses/${b.id}/products/${product.id}/units`, body),
    onSuccess: async () => {
      await client.invalidateQueries({ queryKey: [...tenantKey(b.id), 'units', product.id] });
    },
  });
  return (
    <Modal title={`Units · ${product.name}`} onClose={close}>
      <p className="muted">
        Conversions belong to this product. Saved conversions cannot be changed.
      </p>
      <ErrorNotice error={units.error} />
      {units.isPending ? (
        <Loading />
      ) : (
        <ul className="unit-list">
          {units.data?.map((u) => (
            <li key={u.id}>
              <strong>
                {u.name}
                {u.isBaseUnit && ' (base)'}
              </strong>
              <span>
                {quantity(u.conversionFactor)} {product.stockUnit}
              </span>
            </li>
          ))}
        </ul>
      )}
      {isAdmin(b) && (
        <form
          onSubmit={(event) => {
            event.preventDefault();
            const data = new FormData(event.currentTarget);
            save.mutate({
              unitOfMeasureId: data.get('unit'),
              conversionFactor: Number(data.get('factor')),
              allowsFractionalQuantity: data.get('fractional') === 'on',
            });
          }}
        >
          <h3>Add a pack unit</h3>
          <label>
            Business unit
            <select name="unit" required defaultValue="">
              <option value="" disabled>
                Select a unit
              </option>
              {measures.data
                ?.filter((u) => u.isActive)
                .map((u) => (
                  <option key={u.id} value={u.id}>
                    {u.name}
                  </option>
                ))}
            </select>
          </label>
          <label>
            Base units in this pack
            <input name="factor" type="number" min="0.000001" step="0.000001" required />
          </label>
          <label className="checkbox">
            <input type="checkbox" name="fractional" />
            Allow fractional pack quantities
          </label>
          <ErrorNotice error={measures.error} />
          <SensitiveError error={save.error} />
          {save.isSuccess && (
            <p className="notice success" role="status">
              Pack unit added.
            </p>
          )}
          <Button busy={save.isPending} disabled={!measures.data}>
            Add unit
          </Button>
        </form>
      )}
    </Modal>
  );
}
