using Milvaion.Application.Interfaces;
using Milvasoft.DataAccess.EfCore.Bulk;

namespace Milvaion.Infrastructure.Persistence.Context;

/// <summary>
/// Milvaion scoped factory.
/// </summary>
/// <remarks>
/// Initializes new instance of <see cref="MilvaionDbContextScopedFactory"/>.
/// </remarks>
/// <param name="context"></param>
public class MilvaionDbContextAccessor(MilvaionDbContext context) : IMilvaionDbContextAccessor
{

    /// <summary>
    /// Db context creation implementation.
    /// </summary>
    /// <returns></returns>
    public IMilvaBulkDbContextBase GetDbContext() => context;
}
