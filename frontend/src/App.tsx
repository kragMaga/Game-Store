import { createContext, useContext, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Link, Navigate, NavLink, Outlet, Route, Routes, useParams } from 'react-router-dom';
import {
  Boxes,
  Building2,
  ChevronLeft,
  LayoutDashboard,
  ListTree,
  LogOut,
  Menu,
  Package,
  ShieldCheck,
} from 'lucide-react';
import Auth from './pages/Auth';
import Businesses from './pages/Businesses';
import Dashboard from './pages/Dashboard';
import Products from './pages/Products';
import Masters from './pages/Masters';
import { useSession } from './session';
import { allPages } from './queries';
import { Button, ErrorNotice, Loading } from './components/ui';
import { isAdmin, type Business } from './types';

const BusinessContext = createContext<Business | null>(null);
export function useBusiness() {
  const value = useContext(BusinessContext);
  if (!value) throw new Error('Business context is missing');
  return value;
}
export default function App() {
  const { username } = useSession();
  if (!username) return <Auth />;
  return (
    <Routes>
      <Route path="/" element={<Businesses />} />
      <Route path="/businesses/:businessId" element={<Workspace />}>
        <Route index element={<Navigate to="dashboard" replace />} />
        <Route path="dashboard" element={<Dashboard />} />
        <Route path="products" element={<Products />} />
        <Route path="master-data" element={<Masters />} />
      </Route>
      <Route
        path="*"
        element={
          <main className="business-content">
            <h1>Page not found</h1>
            <Link to="/">Return to your businesses</Link>
          </main>
        }
      />
    </Routes>
  );
}
function Workspace() {
  const { businessId } = useParams();
  const session = useSession();
  const [menu, setMenu] = useState(false);
  const query = useQuery({
    queryKey: ['businesses', 'all'],
    queryFn: ({ signal }) => allPages<Business>('/businesses/', signal),
  });
  const business = query.data?.find((b) => b.id === businessId);
  if (query.isPending) return <Loading />;
  if (query.error)
    return (
      <main className="business-content">
        <ErrorNotice error={query.error} />
        <Button onClick={() => void query.refetch()}>Try again</Button>
        <Link to="/">Back to businesses</Link>
      </main>
    );
  if (!business)
    return (
      <main className="business-content">
        <h1>Workspace unavailable</h1>
        <p>You may no longer have access to this business.</p>
        <Link to="/">Choose another business</Link>
      </main>
    );
  return (
    <BusinessContext.Provider value={business}>
      <div className="workspace">
        <aside className={menu ? 'sidebar open' : 'sidebar'}>
          <Link to="/" className="brand">
            <span className="brand-icon">
              <Boxes />
            </span>
            <span>
              TradeNest
              <br />
              <strong>Business workspace</strong>
            </span>
          </Link>
          <Link to="/" className="workspace-switch">
            <Building2 size={18} />
            <span>
              {business.name}
              <small>{business.currency} workspace · Switch</small>
            </span>
          </Link>
          <div className="nav-label">WORKSPACE</div>
          <nav aria-label="Main navigation">
            {[
              ['dashboard', 'Overview', LayoutDashboard],
              ['products', 'Products', Package],
              ['master-data', 'Master data', ListTree],
            ].map(([path, label, Icon]) => {
              const I = Icon as typeof Package;
              return (
                <NavLink key={String(path)} to={String(path)} onClick={() => setMenu(false)}>
                  <I size={19} />
                  {String(label)}
                </NavLink>
              );
            })}
          </nav>
          <div className="sidebar-note">
            <ShieldCheck size={21} />
            <strong>{business.role} access</strong>
            <p>
              {isAdmin(business)
                ? 'Manage this workspace and its inventory.'
                : 'Your business permissions are managed by an administrator.'}
            </p>
            <Button variant="ghost" onClick={session.reauthenticate}>
              Confirm identity
            </Button>
          </div>
          <Button
            variant="ghost"
            className="signout"
            onClick={() => {
              void session.signOut().catch(() => {});
            }}
          >
            <LogOut size={17} />
            Sign out
          </Button>
        </aside>
        <div className="workspace-main">
          <header className="topbar">
            <Button
              variant="ghost"
              className="mobile-menu"
              aria-label="Toggle navigation"
              aria-expanded={menu}
              onClick={() => setMenu(!menu)}
            >
              <Menu />
            </Button>
            <Link to="/" className="back-link">
              <ChevronLeft size={16} />
              Businesses
            </Link>
            <div className="account">
              <span className="avatar">{session.username?.slice(0, 1).toUpperCase()}</span>
              <span>
                {session.username}
                <small>{business.role}</small>
              </span>
            </div>
          </header>
          <main className="page-content" key={businessId}>
            {business.closedAtUtc ? (
              <div className="notice">
                This business is closed. Its operational workspace is unavailable.{' '}
                <Link to="/">Choose another business.</Link>
              </div>
            ) : (
              <Outlet />
            )}
          </main>
        </div>
      </div>
    </BusinessContext.Provider>
  );
}
