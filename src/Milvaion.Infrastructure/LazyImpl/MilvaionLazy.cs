using Microsoft.Extensions.DependencyInjection;

namespace Milvaion.Infrastructure.LazyImpl;

/// <summary>
/// Custom <see cref="Lazy{T}"/> class.
/// </summary>
/// <typeparam name="T"></typeparam>
/// <remarks>
/// Constructor of <see cref="MilvaionLazy{T}"/>.
/// </remarks>
/// <param name="serviceProvider"></param>
public class MilvaionLazy<T>(IServiceProvider serviceProvider) : Lazy<T>(serviceProvider.GetRequiredService<T>)
{
}
