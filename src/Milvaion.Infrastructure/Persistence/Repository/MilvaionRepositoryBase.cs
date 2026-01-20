using Microsoft.EntityFrameworkCore;
using Milvaion.Application.Interfaces;
using Milvaion.Infrastructure.Persistence.Context;
using Milvasoft.Core.EntityBases.Concrete;
using Milvasoft.Helpers.DataAccess.EfCore.Concrete;
using System.Linq.Expressions;

namespace Milvaion.Infrastructure.Persistence.Repository;

/// <summary>
/// Constructor of <c>BillRepository</c> class.
/// </summary>
/// <param name="dbContext"></param>
public class MilvaionRepositoryBase<TEntity>(MilvaionDbContext dbContext) : BulkBaseRepository<TEntity, MilvaionDbContext>(dbContext), IMilvaionRepositoryBase<TEntity>
    where TEntity : EntityBase
{

    /// <summary>
    /// Gets count.
    /// </summary>
    /// <returns></returns>
    public Task<int> GetCountAsync(Expression<Func<TEntity, bool>> condition = null,
                                         bool tracking = false,
                                         bool splitQuery = false,
                                         CancellationToken cancellationToken = default)
        => QueryWithOptions(tracking, splitQuery).Where(CreateConditionExpression(condition) ?? (entity => true)).CountAsync(cancellationToken: cancellationToken);

    /// <summary>
    /// Checks whether the entity with the given id exists.
    /// </summary>
    /// <returns></returns>
    public Task<bool> AnyAsync(Expression<Func<TEntity, bool>> condition = null,
                                     bool tracking = false,
                                     bool splitQuery = false,
                                     CancellationToken cancellationToken = default)
        => QueryWithOptions(tracking, splitQuery).Where(CreateConditionExpression(condition) ?? (entity => true)).AnyAsync(cancellationToken: cancellationToken);

    /// <summary>
    /// Checks whether the entity with the given id exists.
    /// </summary>
    /// <returns></returns>
    public Task<bool> AnyAsync(object id,
                                     Expression<Func<TEntity, bool>> condition = null,
                                     bool tracking = false,
                                     bool splitQuery = false,
                                     CancellationToken cancellationToken = default)
    {
        var mainCondition = CreateKeyEqualityExpressionWithIsDeletedFalse(id, condition);

        return QueryWithOptions(tracking, splitQuery).Where(mainCondition).AnyAsync(cancellationToken);
    }
}