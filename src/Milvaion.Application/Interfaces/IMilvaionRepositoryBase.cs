using Milvasoft.Core.EntityBases.Concrete;
using Milvasoft.DataAccess.EfCore.Bulk.RepositoryBase.Abstract;
using System.Linq.Expressions;

namespace Milvaion.Application.Interfaces;

/// <summary>
/// Base repository for Milvaion.
/// </summary>
/// <typeparam name="TEntity"></typeparam>
public interface IMilvaionRepositoryBase<TEntity> : IBulkBaseRepository<TEntity> where TEntity : EntityBase
{
    /// <summary>
    /// Gets count.
    /// </summary>
    /// <returns></returns>
    Task<int> GetCountAsync(Expression<Func<TEntity, bool>> condition = null,
                            bool tracking = false,
                            bool splitQuery = false,
                            CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks whether the entity with the given id exists.
    /// </summary>
    /// <returns></returns>
    Task<bool> AnyAsync(Expression<Func<TEntity, bool>> condition = null,
                        bool tracking = false,
                        bool splitQuery = false,
                        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks whether the entity with the given id exists.
    /// </summary>
    /// <returns></returns>
    Task<bool> AnyAsync(object id,
                        Expression<Func<TEntity, bool>> condition = null,
                        bool tracking = false,
                        bool splitQuery = false,
                        CancellationToken cancellationToken = default);
}