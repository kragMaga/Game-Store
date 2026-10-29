import { useQuery } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import {
  ArrowDownLeft,
  ArrowRight,
  CircleDollarSign,
  Package,
  TriangleAlert,
  Wallet,
} from 'lucide-react';
import { api } from '../api';
import { useBusiness } from '../App';
import { tenantKey } from '../queries';
import { Empty, ErrorNotice, Loading, Button } from '../components/ui';
import {
  isAdmin,
  money,
  quantity,
  type Dashboard as DashboardData,
  type Page,
  type Product,
} from '../types';

// Financial dashboard data is requested only for administrators. Staff and viewers
// receive a stock overview through the API routes their memberships permit.
export default function Dashboard() {
  const b = useBusiness();
  const admin = isAdmin(b);
  const dashboard = useQuery({
    queryKey: [...tenantKey(b.id), 'dashboard'],
    queryFn: ({ signal }) => api<DashboardData>(`/businesses/${b.id}/dashboard`, { signal }),
    enabled: admin,
  });
  const stock = useQuery({
    queryKey: [...tenantKey(b.id), 'stock-overview'],
    queryFn: ({ signal }) =>
      api<Page<Product>>(`/businesses/${b.id}/products?lowStockOnly=true&pageSize=10`, { signal }),
    enabled: !admin,
  });
  const error = admin ? dashboard.error : stock.error;
  const loading = admin ? dashboard.isPending : stock.isPending;
  const d = dashboard.data;
  const low =
    d?.lowStock || stock.data?.items.map((p) => ({ ...p, quantity: p.quantityOnHand })) || [];
  return (
    <>
      <div className="page-heading">
        <div>
          <span className="eyebrow">Your business at a glance</span>
          <h1>Overview</h1>
          <p>
            {b.name}
            {d && <> · {d.localDate}</>}
          </p>
        </div>
        <Link className="button button-primary" to="../products">
          <Package size={17} />
          View inventory
          <ArrowRight size={16} />
        </Link>
      </div>
      <ErrorNotice error={error} />
      {error && (
        <Button
          variant="secondary"
          onClick={() => void (admin ? dashboard.refetch() : stock.refetch())}
        >
          Try again
        </Button>
      )}
      {loading ? (
        <Loading />
      ) : (
        !error && (
          <>
            <section className="metrics" aria-label="Business summary">
              {d ? (
                <>
                  <Metric
                    icon={CircleDollarSign}
                    label="Net sales today"
                    value={money(d.netSalesToday, d.currency, d.currencyDecimalPlaces)}
                    hint="Sales less returns"
                  />
                  <Metric
                    icon={Wallet}
                    label="Customer balances"
                    value={money(d.customerDebt, d.currency, d.currencyDecimalPlaces)}
                    hint="Outstanding across customers"
                  />
                  <Metric
                    icon={TriangleAlert}
                    label="Low-stock products"
                    value={quantity(d.lowStockCount)}
                    hint="At or below reorder level"
                    warning={d.lowStockCount > 0}
                  />
                  <Metric
                    icon={ArrowDownLeft}
                    label="Returns today"
                    value={money(d.returnsToday, d.currency, d.currencyDecimalPlaces)}
                    hint="Recorded sale returns"
                  />
                </>
              ) : (
                <Metric
                  icon={TriangleAlert}
                  label="Low-stock products"
                  value={quantity(stock.data?.totalItems || 0)}
                  hint="At or below reorder level"
                />
              )}
            </section>
            <div className="dashboard-grid">
              <section className="panel">
                <div className="panel-heading">
                  <div>
                    <h2>Stock needs attention</h2>
                    <p>Products at or below their reorder level.</p>
                  </div>
                  <span className="badge warning">
                    {d?.lowStockCount ?? stock.data?.totalItems ?? 0} products
                  </span>
                </div>
                {low.length ? (
                  <div className="table-scroll">
                    <table>
                      <thead>
                        <tr>
                          <th>Product</th>
                          <th>On hand</th>
                          <th>Reorder at</th>
                        </tr>
                      </thead>
                      <tbody>
                        {low.map((p) => (
                          <tr key={p.id}>
                            <td>
                              <Link to="../products">{p.name}</Link>
                              <small>{p.sku}</small>
                            </td>
                            <td>
                              <span className="stock-warning">
                                {quantity(p.quantity)} <small>{p.stockUnit}</small>
                              </span>
                            </td>
                            <td>{quantity(p.reorderLevel)}</td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                ) : (
                  <Empty title="Nothing needs reordering">
                    No active products are at or below their reorder level.
                  </Empty>
                )}
                <Link className="panel-footer" to="../products">
                  View all products
                  <ArrowRight size={16} />
                </Link>
              </section>
              <section className="panel">
                <div className="panel-heading">
                  <div>
                    <h2>{admin ? 'Recent activity' : 'Your workspace'}</h2>
                    <p>
                      {admin
                        ? 'The latest updates in this business.'
                        : 'A shared view of your inventory.'}
                    </p>
                  </div>
                </div>
                {d?.recentActivity.length ? (
                  <ul className="activity-list">
                    {d.recentActivity.map((a) => (
                      <li key={a.id}>
                        <span className="activity-dot" />
                        <div>
                          <strong>{a.action.replace(/([a-z])([A-Z])/g, '$1 $2')}</strong>
                          <small>{new Date(a.createdAtUtc).toLocaleString()}</small>
                        </div>
                      </li>
                    ))}
                  </ul>
                ) : (
                  <Empty title="You’re ready to get started">
                    Your business activity will appear here.
                  </Empty>
                )}
                {d && d.failedNotifications > 0 && (
                  <p className="notice error">
                    {d.failedNotifications} notifications need administrator attention.
                  </p>
                )}
              </section>
            </div>
          </>
        )
      )}
    </>
  );
}
function Metric({
  icon: Icon,
  label,
  value,
  hint,
  warning,
}: {
  icon: typeof Package;
  label: string;
  value: string;
  hint: string;
  warning?: boolean;
}) {
  return (
    <article className="metric">
      <div>
        <span>{label}</span>
        <Icon size={18} className={warning ? 'text-warning' : ''} />
      </div>
      <strong>{value}</strong>
      <small>{hint}</small>
    </article>
  );
}
