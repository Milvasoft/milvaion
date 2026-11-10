using Microsoft.EntityFrameworkCore;
using Milvasoft.Components.CQRS.Command;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;

namespace Milvaion.Application.Features.MetricReports.DeleteOldMetricReports;

/// <summary>
/// Deletes old metric reports that are older than a specified number of days.
/// </summary>
/// <param name="milvaionDbContextAccessor"></param>
public class DeleteOldMetricReportsCommandHandler(IMilvaionDbContextAccessor milvaionDbContextAccessor) : IInterceptable, ICommandHandler<DeleteOldMetricReportsCommand, int>
{
    private readonly IMilvaionDbContextAccessor _milvaionDbContextAccessor = milvaionDbContextAccessor;

    /// <inheritdoc />
    public async Task<Response<int>> Handle(DeleteOldMetricReportsCommand request, CancellationToken cancellationToken)
    {
        var cutoffDate = DateTime.UtcNow.AddDays(-request.OlderThanDays);

        var deletedCount = await _milvaionDbContextAccessor.GetDbContext()
                                                           .Set<MetricReport>()
                                                           .Where(r => r.GeneratedAt < cutoffDate)
                                                           .ExecuteDeleteAsync(cancellationToken);

        return Response<int>.Success(deletedCount);
    }
}
