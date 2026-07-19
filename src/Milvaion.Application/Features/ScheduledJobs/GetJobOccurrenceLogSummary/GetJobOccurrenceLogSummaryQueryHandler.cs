using Microsoft.EntityFrameworkCore;
using Milvaion.Application.Dtos.ScheduledJobDtos;
using Milvasoft.Components.CQRS.Query;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;
using Milvasoft.DataAccess.EfCore.Bulk;

namespace Milvaion.Application.Features.ScheduledJobs.GetJobOccurrenceLogSummary;

/// <summary>
/// Aggregates execution logs so they can be reasoned about without being read.
/// </summary>
/// <remarks>
/// Every breakdown is a GROUP BY executed by the database. Nothing here loads log rows into
/// memory, which is the point: the log table is the largest in the system and the answer to
/// "what is going wrong" has to stay the same size whether there are a thousand lines or a
/// hundred million.
///
/// The window is mandatory in effect - when the caller gives no bounds a day is assumed -
/// because these aggregates are only cheap when the time column narrows the scan first.
/// </remarks>
/// <param name="milvaionDbContextAccessor"></param>
public class GetJobOccurrenceLogSummaryQueryHandler(IMilvaionDbContextAccessor milvaionDbContextAccessor) : IInterceptable, IQueryHandler<GetJobOccurrenceLogSummaryQuery, JobOccurrenceLogSummaryDto>
{
    private readonly IMilvaionDbContextAccessor _milvaionDbContextAccessor = milvaionDbContextAccessor;

    /// <summary>
    /// Severity treated as a failure when counting a job's errors.
    /// </summary>
    private const string _errorLevel = "Error";

    /// <inheritdoc/>
    public async Task<Response<JobOccurrenceLogSummaryDto>> Handle(GetJobOccurrenceLogSummaryQuery request, CancellationToken cancellationToken)
    {
        var context = _milvaionDbContextAccessor.GetDbContext();

        var until = request.Until ?? DateTime.UtcNow;
        var since = request.Since ?? until.AddHours(-24);
        var topCount = Math.Clamp(request.TopCount, 1, 50);

        var query = BuildQuery(context, request, since, until);

        var result = new JobOccurrenceLogSummaryDto
        {
            Since = since,
            Until = until,
            TotalCount = await query.CountAsync(cancellationToken)
        };

        if (result.TotalCount == 0)
            return Response<JobOccurrenceLogSummaryDto>.Success(result);

        result.ByLevel = await query.GroupBy(l => l.Level)
                                    .Select(g => new LogCountDto { Value = g.Key, Count = g.Count() })
                                    .OrderByDescending(c => c.Count)
                                    .ToListAsync(cancellationToken);

        result.ByCategory = await query.GroupBy(l => l.Category)
                                       .Select(g => new LogCountDto { Value = g.Key, Count = g.Count() })
                                       .OrderByDescending(c => c.Count)
                                       .Take(topCount)
                                       .ToListAsync(cancellationToken);

        result.ByExceptionType = await query.Where(l => l.ExceptionType != null)
                                            .GroupBy(l => l.ExceptionType)
                                            .Select(g => new LogCountDto { Value = g.Key, Count = g.Count() })
                                            .OrderByDescending(c => c.Count)
                                            .Take(topCount)
                                            .ToListAsync(cancellationToken);

        result.TopJobs = await query.GroupBy(l => new { l.Occurrence.JobId, l.Occurrence.JobName })
                                    .Select(g => new LogJobCountDto
                                    {
                                        JobId = g.Key.JobId,
                                        JobName = g.Key.JobName,
                                        Count = g.Count(),
                                        ErrorCount = g.Count(l => l.Level == _errorLevel)
                                    })
                                    .OrderByDescending(j => j.Count)
                                    .Take(topCount)
                                    .ToListAsync(cancellationToken);

        result.TopMessages = await query.GroupBy(l => new { l.Message, l.Level })
                                        .Select(g => new LogMessageCountDto
                                        {
                                            Message = g.Key.Message,
                                            Level = g.Key.Level,
                                            Count = g.Count(),
                                            LastSeen = g.Max(l => l.Timestamp)
                                        })
                                        .OrderByDescending(m => m.Count)
                                        .Take(topCount)
                                        .ToListAsync(cancellationToken);

        return Response<JobOccurrenceLogSummaryDto>.Success(result);
    }

    private static IQueryable<JobOccurrenceLog> BuildQuery(IMilvaBulkDbContextBase context,
                                                           GetJobOccurrenceLogSummaryQuery request,
                                                           DateTime since,
                                                           DateTime until)
    {
        var query = context.Set<JobOccurrenceLog>()
                           .AsNoTracking()
                           .Where(l => l.Timestamp >= since && l.Timestamp <= until);

        if (request.JobId.HasValue)
            query = query.Where(l => l.Occurrence.JobId == request.JobId.Value);

        if (!string.IsNullOrWhiteSpace(request.Level))
            query = query.Where(l => l.Level == request.Level);

        if (!string.IsNullOrWhiteSpace(request.SearchTerm))
        {
            var searchTerm = $"%{request.SearchTerm.Trim()}%";

            query = query.Where(l => EF.Functions.ILike(l.Message, searchTerm));
        }

        return query;
    }
}
