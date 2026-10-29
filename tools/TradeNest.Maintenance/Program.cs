using TradeNest.Api.Data;

// Explicit local commands, never an unauthenticated database-download endpoint.
// Paths/configuration come from operator arguments; API secrets are not required.
if (args.Length == 0 ||
    !(args[0] == "backup" && args.Length == 3 || args[0] == "verify" && args.Length == 2))
{
    Console.Error.WriteLine("Usage: backup <source.db> <new-backup.db> | verify <backup.db>");
    return 2;
}
try
{
    if (args[0] == "backup") await SqliteBackup.CreateVerifiedAsync(args[1], args[2]);
    else await SqliteBackup.VerifyAsync(args[1]);
    Console.WriteLine("Database verification passed.");
    return 0;
}
catch (Exception exception)
{
    // Avoid printing connection strings, SQL, account data, or filesystem paths.
    Console.Error.WriteLine($"Maintenance failed ({exception.GetType().Name}). No successful backup is reported.");
    return 1;
}
