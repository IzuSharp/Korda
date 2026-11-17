using NativeWeave.Database;

internal static class Advanced
{
    // Keep an explicit deployment target; later calls retain all previously applied entries.
    internal static async Task<MigrationReport> DeployThroughAsync(DataSource source, MigrationBundle bundle, long target, CancellationToken cancellationToken)
    {
        await source.Administration.ValidateMigrationsAsync(bundle, cancellationToken);
        return await source.Administration.MigrateAsync(bundle, new() { TargetVersion = target, LockTimeout = TimeSpan.FromSeconds(15) }, cancellationToken);
    }
    // This is a deliberate operational opt-in. Before resuming an unfinished nontransactional
    // phase, inspect/repair its partial effects and keep the exact same manifest bytes.
    internal static Task<MigrationReport> ResumeReviewedPostgreSqlAsync(DataSource source, MigrationBundle reviewedBundle, CancellationToken cancellationToken)
        => source.Administration.MigrateAsync(reviewedBundle, new() { AllowNonTransactional = true }, cancellationToken);
}
