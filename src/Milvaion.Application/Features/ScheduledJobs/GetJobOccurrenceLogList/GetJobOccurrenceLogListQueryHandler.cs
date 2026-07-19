using Microsoft.EntityFrameworkCore;
using Milvaion.Application.Dtos.ScheduledJobDtos;
using Milvasoft.Components.CQRS.Query;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;
using Milvasoft.DataAccess.EfCore.Bulk;

namespace Milvaion.Application.Features.ScheduledJobs.GetJobOccurrenceLogList;

/// <summary>
/// Searches execution log lines.
/// </summary>
/// <remarks>
/// The log table is the largest in the system - one row per line per execution - so every
/// query here is bounded and ordered, and the total count is only computed when the filters
/// make it cheap. An unbounded <c>COUNT(*)</c> over this table is a full scan.
/// </remarks>
/// <param name="milvaionDbContextAccessor"></param>
public class GetJobOccurrenceLogListQueryHandler(IMilvaionDbContextAccessor milvaionDbContextAccessor) : IInterceptable, IQueryHandler<GetJobOccurrenceLogListQuery, JobOccurrenceLogSearchDto>
{
    private readonly IMilvaionDbContextAccessor _milvaionDbContextAccessor = milvaionDbContextAccessor;

    /// <inheritdoc/>
    public async Task<Response<JobOccurrenceLogSearchDto>> Handle(GetJobOccurrenceLogListQuery request, CancellationToken cancellationToken)
    {
        var context = _milvaionDbContextAccessor.GetDbContext();

        var pageNumber = request.PageNumber < 1 ? 1 : request.PageNumber;
        var rowCount = Math.Clamp(request.RowCount, 1, 200);

        var query = BuildQuery(context, request);

        // One row past the page, so "is there more" is answered without a second query.
        var rows = await query.OrderByDescending(l => l.Timestamp)
                              .ThenByDescending(l => l.Id)
                              .Skip((pageNumber - 1) * rowCount)
                              .Take(rowCount + 1)
                              .Select(l => new LogRow
                              {
                                  Id = l.Id,
                                  OccurrenceId = l.OccurrenceId,
                                  JobId = l.Occurrence.JobId,
                                  JobName = l.Occurrence.JobName,
                                  Timestamp = l.Timestamp,
                                  Level = l.Level,
                                  Category = l.Category,
                                  Message = l.Message,
                                  ExceptionType = l.ExceptionType,
                                  Data = l.Data
                              })
                              .ToListAsync(cancellationToken);

        var hasMore = rows.Count > rowCount;

        if (hasMore)
            rows.RemoveAt(rows.Count - 1);

        var result = new JobOccurrenceLogSearchDto
        {
            PageNumber = pageNumber,
            HasMore = hasMore,
            DataIncluded = request.IncludeData,
            Logs = [.. rows.Select(r => ToDto(r, request.IncludeData))]
        };

        // Counting is only worth it when something narrows the scan. Asking "how many log
        // lines exist in total" is not a question worth a full table scan to answer.
        if (IsNarrowEnoughToCount(request))
            result.TotalCount = await BuildQuery(context, request).CountAsync(cancellationToken);

        return Response<JobOccurrenceLogSearchDto>.Success(result);
    }

    private static IQueryable<JobOccurrenceLog> BuildQuery(IMilvaBulkDbContextBase context, GetJobOccurrenceLogListQuery request)
    {
        var query = context.Set<JobOccurrenceLog>().AsNoTracking();

        if (request.OccurrenceId.HasValue)
            query = query.Where(l => l.OccurrenceId == request.OccurrenceId.Value);

        if (request.JobId.HasValue)
            query = query.Where(l => l.Occurrence.JobId == request.JobId.Value);

        if (!string.IsNullOrWhiteSpace(request.Level))
            query = query.Where(l => l.Level == request.Level);

        if (!string.IsNullOrWhiteSpace(request.Category))
            query = query.Where(l => l.Category == request.Category);

        if (!string.IsNullOrWhiteSpace(request.ExceptionType))
            query = query.Where(l => l.ExceptionType == request.ExceptionType);

        if (request.Since.HasValue)
            query = query.Where(l => l.Timestamp >= request.Since.Value);

        if (request.Until.HasValue)
            query = query.Where(l => l.Timestamp <= request.Until.Value);

        if (!string.IsNullOrWhiteSpace(request.SearchTerm))
        {
            var searchTerm = $"%{request.SearchTerm.Trim()}%";

            query = query.Where(l => EF.Functions.ILike(l.Message, searchTerm));
        }

        return query;
    }

    /// <summary>
    /// Whether the filters bound the scan enough for a count to be worth running.
    /// </summary>
    private static bool IsNarrowEnoughToCount(GetJobOccurrenceLogListQuery request)
        => request.OccurrenceId.HasValue || request.JobId.HasValue || request.Since.HasValue;

    private static JobOccurrenceLogListDto ToDto(LogRow row, bool includeData) => new()
    {
        Id = row.Id,
        OccurrenceId = row.OccurrenceId,
        JobId = row.JobId,
        JobName = row.JobName,
        Timestamp = row.Timestamp,
        Level = row.Level,
        Category = row.Category,
        Message = row.Message,
        ExceptionType = row.ExceptionType,

        // Names always, values only on request. Seeing that a line carries "OrderId" and
        // "CustomerEmail" is usually enough to reason about it, and it does not hand the
        // contents to whoever is reading.
        DataKeys = row.Data is null ? [] : [.. row.Data.Keys],
        Data = includeData ? row.Data : null
    };

    private sealed class LogRow
    {
        public Guid Id { get; set; }
        public Guid OccurrenceId { get; set; }
        public Guid JobId { get; set; }
        public string JobName { get; set; }
        public DateTime Timestamp { get; set; }
        public string Level { get; set; }
        public string Category { get; set; }
        public string Message { get; set; }
        public string ExceptionType { get; set; }
        public Dictionary<string, object> Data { get; set; }
    }
}
