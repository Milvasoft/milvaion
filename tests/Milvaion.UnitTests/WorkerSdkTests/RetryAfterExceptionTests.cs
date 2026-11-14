using FluentAssertions;
using Milvasoft.Milvaion.Sdk.Worker.Exceptions;

namespace Milvaion.UnitTests.WorkerSdkTests;

[Trait("SDK Unit Tests", "RetryAfterException unit tests.")]
public class RetryAfterExceptionTests
{
    [Fact]
    public void Constructor_WithUtcTime_ShouldKeepIt()
    {
        var at = new DateTime(2026, 11, 15, 11, 0, 0, DateTimeKind.Utc);

        var ex = new RetryAfterException(at, "Quota used up");

        ex.RetryAt.Should().Be(at);
        ex.RetryAt.Kind.Should().Be(DateTimeKind.Utc);
        ex.Message.Should().Be("Quota used up");
    }

    [Fact]
    public void Constructor_WithUnspecifiedKind_ShouldTreatItAsUtc()
    {
        var ex = new RetryAfterException(new DateTime(2026, 11, 15, 11, 0, 0, DateTimeKind.Unspecified));

        ex.RetryAt.Should().Be(new DateTime(2026, 11, 15, 11, 0, 0, DateTimeKind.Utc));
        ex.RetryAt.Kind.Should().Be(DateTimeKind.Utc);
        ex.Message.Should().Contain("2026-11-15T11:00:00");
    }

    [Fact]
    public void Constructor_WithOffset_ShouldConvertToUtc()
    {
        var ex = new RetryAfterException(new DateTimeOffset(2026, 11, 15, 14, 0, 0, TimeSpan.FromHours(3)), "Quota used up");

        ex.RetryAt.Should().Be(new DateTime(2026, 11, 15, 11, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Constructor_WithInnerException_ShouldKeepIt()
    {
        var inner = new HttpRequestException("429");

        var ex = new RetryAfterException(DateTime.UtcNow.AddHours(1), "Quota used up", inner);

        ex.InnerException.Should().BeSameAs(inner);
    }
}
