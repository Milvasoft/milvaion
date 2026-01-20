using Milvaion.Application.Dtos.ScheduledJobDtos;
using Milvasoft.Components.CQRS.Query;
using Milvasoft.Components.Rest.Enums;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;

namespace Milvaion.Application.Features.ScheduledJobs.GetJobOccurenceDetail;

/// <summary>
/// Handles the job occurrence detail operation.
/// </summary>
/// <param name="jobOccurrenceRepository"></param>
public class GetJobOccurrenceDetailQueryHandler(IMilvaionRepositoryBase<JobOccurrence> jobOccurrenceRepository) : IInterceptable, IQueryHandler<GetJobOccurrenceDetailQuery, JobOccurrenceDetailDto>
{
    private readonly IMilvaionRepositoryBase<JobOccurrence> _jobOccurrenceRepository = jobOccurrenceRepository;

    /// <inheritdoc/>
    public async Task<Response<JobOccurrenceDetailDto>> Handle(GetJobOccurrenceDetailQuery request, CancellationToken cancellationToken)
    {
        var scheduledjob = await _jobOccurrenceRepository.GetByIdAsync(request.OccurrenceId, projection: JobOccurrenceDetailDto.Projection, cancellationToken: cancellationToken);

        if (scheduledjob == null)
            return Response<JobOccurrenceDetailDto>.Success(scheduledjob, MessageKey.JobNotFound, MessageType.Warning);

        return Response<JobOccurrenceDetailDto>.Success(scheduledjob);
    }
}
