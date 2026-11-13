using Microsoft.Extensions.Options;
using Milvaion.Application.Interfaces.Secrets;

namespace Milvaion.UnitTests.InfrastructureTests.Secrets;

internal sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue { get; set; } = value;

    public T Get(string name) => CurrentValue;

    public IDisposable OnChange(Action<T, string> listener) => null;
}

internal sealed class ManualTimeProvider : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 11, 14, 9, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>
/// In-memory provider that counts calls and can be told to fail.
/// </summary>
internal sealed class FakeSecretProvider(string name, Dictionary<string, string> values = null) : ISecretProvider
{
    public string Name { get; } = name;

    public Dictionary<string, string> Values { get; } = values ?? [];

    public Exception FailWith { get; set; }

    public List<string> Requests { get; } = [];

    public Task<string> GetSecretAsync(string name, CancellationToken cancellationToken = default)
    {
        Requests.Add(name);

        if (FailWith != null)
            throw FailWith;

        return Task.FromResult(Values.GetValueOrDefault(name));
    }
}
