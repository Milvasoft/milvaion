using EFCore.BulkExtensions;
using Microsoft.EntityFrameworkCore;
using Milvaion.Domain;
using Milvaion.Infrastructure.Persistence.Context;
using Npgsql;

namespace Milvaion.Infrastructure.Persistence;

/// <summary>
/// Concurrency control for the many writers of <see cref="JobOccurrence"/> rows.
/// </summary>
/// <remarks>
/// EFCore.BulkExtensions does not apply concurrency tokens, so a row version would not protect the bulk
/// updates these services use. Instead the rows are locked and re-checked in the transaction that writes
/// them, which turns read-then-write into a single atomic step.
/// </remarks>
public static class JobOccurrenceConcurrencyExtensions
{
    /// <summary>
    /// The state of an occurrence row as seen under its row lock.
    /// </summary>
    public sealed class LockedOccurrenceRow
    {
        /// <summary>Occurrence id.</summary>
        public Guid Id { get; set; }

        /// <summary>Status currently stored in the database.</summary>
        public JobOccurrenceStatus Status { get; set; }

        /// <summary>Heartbeat currently stored in the database.</summary>
        public DateTime? LastHeartbeat { get; set; }
    }

    /// <summary>
    /// Bulk-updates only the occurrences whose current database state still allows the write.
    /// Rows that no longer exist are skipped.
    /// </summary>
    /// <param name="dbContext">Context to write with.</param>
    /// <param name="occurrences">Candidates carrying the new values.</param>
    /// <param name="canWrite">Receives the candidate and its locked row; returns whether the write may proceed.</param>
    /// <param name="configure">Bulk configuration.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The candidates that were not written.</returns>
    public static async Task<List<JobOccurrence>> BulkUpdateGuardedAsync(this MilvaionDbContext dbContext,
                                                                         List<JobOccurrence> occurrences,
                                                                         Func<JobOccurrence, LockedOccurrenceRow, bool> canWrite,
                                                                         Action<BulkConfig> configure,
                                                                         CancellationToken cancellationToken)
    {
        if (occurrences.Count == 0)
            return [];

        // A consistent lock order prevents deadlocks between concurrent batches.
        var ordered = occurrences.OrderBy(o => o.Id).ToList();
        var ids = ordered.Select(o => o.Id).ToArray();

        return await dbContext.Database.CreateExecutionStrategy().ExecuteAsync(async ct =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(ct);

            var rows = await dbContext.Database
                                      .SqlQueryRaw<LockedOccurrenceRow>($"""SELECT "Id", "Status", "LastHeartbeat" FROM "{TableNames.JobOccurrences}" WHERE "Id" = ANY(@ids) ORDER BY "Id" FOR UPDATE""",
                                                                        new NpgsqlParameter("ids", ids))
                                      .ToDictionaryAsync(r => r.Id, ct);

            var toWrite = new List<JobOccurrence>(ordered.Count);
            var rejected = new List<JobOccurrence>();

            foreach (var occurrence in ordered)
            {
                if (rows.TryGetValue(occurrence.Id, out var row) && canWrite(occurrence, row))
                    toWrite.Add(occurrence);
                else
                    rejected.Add(occurrence);
            }

            if (toWrite.Count > 0)
                await dbContext.BulkUpdateAsync(toWrite, configure, cancellationToken: ct);

            await transaction.CommitAsync(ct);

            return rejected;
        }, cancellationToken);
    }
}
