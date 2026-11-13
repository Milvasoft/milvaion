using FluentAssertions;
using Milvaion.Application.Utils.Models.Options;
using Milvaion.Infrastructure.Services.Secrets;
using Milvasoft.Milvaion.Sdk.Domain.JsonModels;
using System.Text.Json;

namespace Milvaion.UnitTests.InfrastructureTests.Secrets;

[Trait("Infrastructure Unit Tests", "Masking secret values that workers send back.")]
public class SecretMaskerTests
{
    private readonly TestOptionsMonitor<SecretsOptions> _options = new(new SecretsOptions());
    private readonly ManualTimeProvider _time = new();
    private readonly SecretMasker _masker;

    public SecretMaskerTests() => _masker = new SecretMasker(_options, _time);

    [Fact]
    public void Mask_WithNothingRegistered_ShouldReturnTheSameText()
    {
        const string text = "nothing to see";

        _masker.Mask(text).Should().BeSameAs(text);
    }

    [Fact]
    public void Mask_ShouldReplaceEveryOccurrenceWithTheReference()
    {
        _masker.Register("{{secret:smtp-password}}", "hunter2!");

        _masker.Mask("auth hunter2! failed, retry with hunter2!")
               .Should().Be("auth {{secret:smtp-password}} failed, retry with {{secret:smtp-password}}");
    }

    [Fact]
    public void Mask_ShouldReplaceTheJsonEscapedFormInsideDocuments()
    {
        const string value = "pa\"ss\\word";
        _masker.Register("{{secret:db}}", value);

        var echoed = JsonSerializer.Serialize(new { password = value });

        var masked = _masker.Mask(echoed);

        masked.Should().NotContain("pa\\\"ss");
        JsonDocument.Parse(masked).RootElement.GetProperty("password").GetString().Should().Be("{{secret:db}}");
    }

    [Fact]
    public void Register_ShouldIgnoreValuesShorterThanTheMinimum()
    {
        _masker.Register("{{secret:flag}}", "on");

        _masker.Mask("turn it on").Should().Be("turn it on");
        _masker.Count.Should().Be(0);
    }

    [Fact]
    public void Mask_ShouldReplaceLongerValuesFirst()
    {
        _masker.Register("{{secret:short}}", "abcd");
        _masker.Register("{{secret:long}}", "abcd-efgh");

        _masker.Mask("x abcd-efgh y abcd").Should().Be("x {{secret:long}} y {{secret:short}}");
    }

    [Fact]
    public void Mask_ShouldForgetValuesAfterTheRetentionPeriod()
    {
        _options.CurrentValue.MaskRetentionMinutes = 10;
        _masker.Register("{{secret:db}}", "old-password");

        _time.Advance(TimeSpan.FromMinutes(11));

        _masker.Mask("old-password").Should().Be("old-password");
        _masker.Count.Should().Be(0);
    }

    [Fact]
    public void MaskLog_ShouldMaskMessageAndData()
    {
        _masker.Register("{{secret:api-token}}", "tok_live_123");

        var log = new OccurrenceLog
        {
            Message = "Calling API with tok_live_123",
            Data = new Dictionary<string, object> { ["header"] = "Bearer tok_live_123", ["count"] = 3 }
        };

        _masker.MaskLog(log);

        log.Message.Should().Be("Calling API with {{secret:api-token}}");
        JsonSerializer.Serialize(log.Data).Should().NotContain("tok_live_123").And.Contain("Bearer {{secret:api-token}}");
    }

    [Fact]
    public void MaskData_WithoutSecretValues_ShouldReturnTheSameDictionary()
    {
        _masker.Register("{{secret:api-token}}", "tok_live_123");
        var data = new Dictionary<string, object> { ["count"] = 3 };

        _masker.MaskData(data).Should().BeSameAs(data);
    }
}
