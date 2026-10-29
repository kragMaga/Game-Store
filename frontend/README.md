# TradeNest frontend

React + TypeScript, Vite, React Router, TanStack Query, Tailwind CSS and local,
customizable UI primitives. This is a separate application in the same repository.
It calls the existing .NET API; no backend behavior or live database was changed.

## Run locally

Use Node.js 22.19 or newer supported by the installed Vite version. Dependencies
are locked in package-lock.json; use npm ci to reproduce the installation.

Start the configured backend in one terminal from the repository root:

```powershell
dotnet run --project TradeNest.Api --launch-profile http
```

API startup applies pending migrations. Configure backend secrets/email using the
root README before starting it; production does not seed an administrator account.

In another terminal:

```powershell
cd frontend
npm ci
npm run dev
```

Open the Vite URL (normally http://localhost:5173). If the API uses another port,
copy .env.example to .env.local and change API_PROXY_TARGET, then restart Vite.
No broad CORS changes are needed: /api requests go through the Vite proxy, which
removes /api before forwarding them to the backend. No API secrets belong in VITE_*
environment variables, because those values become public browser code.

## What this batch covers

- Registration, sign-in, MFA challenges, resend-confirmation, and password-reset requests.
- Business creation, membership-based selection/switching and explicit unavailable states.
- Live Owner/Admin dashboard; a stock-only overview for Staff/Viewer memberships.
- Product search, paging, low-stock filter, create/edit/activate/deactivate.
- Business-owned category and UOM lists with create, rename and deactivate/reactivate.
- Product-specific pack conversions and one-time onboarding stock.
- Recent-authentication prompts, normalized errors, and rate-limit retry information.
- Responsive navigation, keyboard-accessible forms and native modal dialogs.

Email links continue using the backend's existing confirmation/reset pages.
Purchase/sale entry, customer/payment screens, reporting screens, invitations,
settings and detailed notification management are subsequent frontend batches.
The backend already provides these capabilities; this batch does not expose them all.

## Session and tenant boundaries

Access/refresh tokens and recent-auth proofs remain in module memory. No credentials
are saved to localStorage, sessionStorage or IndexedDB. Reloading/closing the tab
requires signing in again; automatic persistent-session restoration is not implemented
in this batch. Rotating-token refresh keeps an open tab working when access expires.
Concurrent failed requests share one refresh, and a late response cannot restore a
logged-out session. Sign-out clears local state even if server revocation fails.

Both existing token responses and optional refresh-cookie responses are supported.
Cookie mode requires HTTPS as configured by the backend and forwards X-CSRF-TOKEN
on refresh/logout. A future reload-persistent cookie session needs a deliberate
CSRF/session-bootstrap flow; do not add browser token storage as a workaround.

Every inventory query cache key contains BusinessId. Switching business routes
remounts unsaved forms, and account changes clear all query caches. The API rechecks
live membership and role on every request; client-side visibility is convenience,
not authorization. Master selectors send foreign-key IDs and load all bounded pages,
not just the first 100 options. Read errors never become fake zero financial totals.

Mutations are not automatically retried. Opening-stock attempts reuse an idempotency
key when retrying the same request after an uncertain response. Pack conversions and
base-unit meaning cannot be edited. Existing backend validation remains authoritative.

## Source map

| File                  | What it explains                                                  |
| --------------------- | ----------------------------------------------------------------- |
| src/api.ts            | In-memory credentials, token rotation, CSRF and normalized errors |
| src/session.tsx       | Account state, cache clearing and recent-auth confirmation        |
| src/App.tsx           | Routes, workspace membership selection and responsive navigation  |
| src/queries.ts        | Business cache keys and complete paged dropdown loading           |
| src/pages/            | Business onboarding, dashboard, product and master-data flows     |
| src/components/ui.tsx | Shared buttons, dialogs, error states and pagination              |
| src/styles.css        | Design tokens, responsive layout and component styling            |
| vite.config.ts        | Development-only same-origin API proxy                            |

Comments explain the security/state choices and why relationships are sent as IDs.

## Validation

```powershell
npm run build
npm test
npm run format:check
```

Tests use an isolated mocked API and jsdom. They cover refresh concurrency,
logout/refresh races, cookie CSRF headers, MFA challenges and product master-data IDs.
They do not replace a real-browser test against a staging API. The production build
includes strict TypeScript checking. No real users or database records are created
by the frontend tests.

## Deployment

Build with npm run build and serve dist/ over HTTPS. Route /api/* to the .NET API,
stripping /api; configure SPA fallback to index.html for frontend routes only.
API failures must not fall back to index.html. Vite's development proxy is not a
production reverse proxy. Follow the backend's existing trusted-proxy configuration
when supplying forwarded client-IP/protocol headers. Vite preview is for local build
checks, not production hosting.
