using Microsoft.EntityFrameworkCore;
using Milvasoft.DataAccess.EfCore.Configuration;

namespace Milvaion.Infrastructure.Persistence.Context;

/// <summary>
/// Milvaion scoped factory.
/// </summary>
/// <remarks>
/// Initializes new instance of <see cref="MilvaionDbContextScopedFactory"/>.
/// </remarks>
/// <param name="pooledFactory"></param>
/// <param name="dataAccessConfiguration"></param>
/// <param name="serviceProvider"></param>
public class MilvaionDbContextScopedFactory(IDbContextFactory<MilvaionDbContext> pooledFactory,
                                           IDataAccessConfiguration dataAccessConfiguration,
                                           IServiceProvider serviceProvider) : IDbContextFactory<MilvaionDbContext>
{

    /// <summary>
    /// Db context creation implementation.
    /// </summary>
    /// <returns></returns>
    public MilvaionDbContext CreateDbContext()
    {
        var context = pooledFactory.CreateDbContext();

        context.ServiceProvider = serviceProvider;
        context.SetDataAccessConfiguration(dataAccessConfiguration);

        return context;
    }
}
