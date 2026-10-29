# Legacy catalog retirement

The application now serves business inventory and account security only.
Games/Genres endpoints, DTOs, mappings, catalog caching/search, HTTP samples,
global user listing/role changes/deletion, global JWT role claims, and production
bootstrap admin seeding have been removed. Legacy configuration switches cannot
restore those routes. Business Owner/Admin/Staff/Viewer permissions remain intact.
`/auth/me` identifies the authenticated account without a global roles list.

Startup applies migrations; it does not create a privileged user. A user registers,
confirms their email and creates a business to become its owner. Account closure is
the retained deactivation workflow; arbitrary global account deletion is absent.
Authentication, MFA, session revocation, and recent-auth checks remain in place.

## Permanent legacy-data removal

Historical migrations remain available so existing databases can upgrade.
`RetireLegacyCatalogAndGlobalRoles` first archives the old catalog/global roles and
removes catalog search structures. `RemoveLegacyArchives` then drops ArchivedGames,
ArchivedGenres, and ArchivedGlobalRoles, permanently deleting their contents.
No catalog rows are imported into a business.

Business inventory, customers/debts, document numbers, accounts/password hashes,
MFA keys, memberships and email outbox records are retained. Rolling back the removal
migration recreates empty archive schemas for historical migration compatibility;
it cannot recover deleted rows. Existing backup files are not modified or erased.
New whole-database backups contain no retired catalog archives. Active-schema
verification still requires 30 inventory/authentication/migration tables.

No live database migration was applied during this change. Startup applies the
pending removal migration when the application next runs against that database.

## TradeNest naming

API/test/maintenance projects, source namespaces, frontend branding and documentation
now use TradeNest. Persisted security identifiers, the existing secrets-store ID and
the database filename retain their legacy values for compatibility. See the
[rename guide](tradenest-rename.md) for details. Existing authenticator entries still
work; newly enrolled authenticators use the TradeNest display name.

Tests seed a confirmed account named Admin solely inside the isolated test host.
It has no global admin authority. Production contains no equivalent bootstrap path.
