using Milvasoft.DataAccess.EfCore.Bulk;

namespace Milvaion.Application.Interfaces;

/// <summary>
/// Interface for MilvaionDbContextAccessor.
/// </summary>
public interface IMilvaionDbContextAccessor
{
    /// <summary>
    /// Get db context.
    /// </summary>
    /// <returns></returns>
    IMilvaBulkDbContextBase GetDbContext();
}