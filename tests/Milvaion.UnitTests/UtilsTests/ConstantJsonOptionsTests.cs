using FluentAssertions;
using Milvaion.Application.Utils.Constants;
using System.Text.Json;

namespace Milvaion.UnitTests.UtilsTests;

[Trait("Utils Unit Tests", "ConstantJsonOptions unit tests.")]
public class ConstantJsonOptionsTests
{
    [Fact]
    public void PropNameCaseInsensitive_ShouldNotBeNull()
        // Assert
        => ConstantJsonOptions.PropNameCaseInsensitive.Should().NotBeNull();

    [Fact]
    public void PropNameCaseInsensitive_ShouldHavePropertyNameCaseInsensitiveTrue()
        // Assert
        => ConstantJsonOptions.PropNameCaseInsensitive.PropertyNameCaseInsensitive.Should().BeTrue();

    [Fact]
    public void PropNameCaseInsensitive_ShouldDeserializeCaseInsensitively()
    {
        // Arrange
        var json = """{"NAME": "test", "VALUE": 123}""";

        // Act
        var result = JsonSerializer.Deserialize<TestDto>(json, ConstantJsonOptions.PropNameCaseInsensitive);

        // Assert
        result.Should().NotBeNull();
        result.Name.Should().Be("test");
        result.Value.Should().Be(123);
    }

    [Fact]
    public void PropNameCaseInsensitive_ShouldDeserializeLowerCase()
    {
        // Arrange
        var json = """{"name": "test", "value": 123}""";

        // Act
        var result = JsonSerializer.Deserialize<TestDto>(json, ConstantJsonOptions.PropNameCaseInsensitive);

        // Assert
        result.Should().NotBeNull();
        result.Name.Should().Be("test");
        result.Value.Should().Be(123);
    }

    [Fact]
    public void PropNameCaseInsensitive_ShouldDeserializeMixedCase()
    {
        // Arrange
        var json = """{"NaMe": "test", "vAlUe": 123}""";

        // Act
        var result = JsonSerializer.Deserialize<TestDto>(json, ConstantJsonOptions.PropNameCaseInsensitive);

        // Assert
        result.Should().NotBeNull();
        result.Name.Should().Be("test");
        result.Value.Should().Be(123);
    }

    [Fact]
    public void PropNameCaseInsensitive_ShouldBeSameInstance()
    {
        // Act
        var options1 = ConstantJsonOptions.PropNameCaseInsensitive;
        var options2 = ConstantJsonOptions.PropNameCaseInsensitive;

        // Assert
        options1.Should().BeSameAs(options2);
    }

    private class TestDto
    {
        public string Name { get; set; }
        public int Value { get; set; }
    }
}

[Trait("Utils Unit Tests", "GlobalConstant unit tests.")]
public class GlobalConstantTests
{
    [Fact]
    public void RoutePrefix_ShouldBeApi()
        // Assert
        => GlobalConstant.RoutePrefix.Should().Be("api");

    [Fact]
    public void RouteBase_ShouldContainVersionPlaceholder()
        // Assert
        => GlobalConstant.RouteBase.Should().Contain("v{version:apiVersion}");

    [Fact]
    public void FullRoute_ShouldContainControllerPlaceholder()
        // Assert
        => GlobalConstant.FullRoute.Should().Contain("[controller]");

    [Fact]
    public void WWWRoot_ShouldBeWwwroot()
        // Assert
        => GlobalConstant.WWWRoot.Should().Be("wwwroot");

    [Fact]
    public void Http_ShouldBeHttp()
        // Assert
        => GlobalConstant.Http.Should().Be("http");

    [Fact]
    public void Https_ShouldBeHttps()
        // Assert
        => GlobalConstant.Https.Should().Be("https");

    [Fact]
    public void DefaultApiVersion_ShouldBeV1()
        // Assert
        => GlobalConstant.DefaultApiVersion.Should().Be("v1.0");

    [Fact]
    public void CurrentApiVersion_ShouldBe1()
        // Assert
        => GlobalConstant.CurrentApiVersion.Should().Be("1.0");

    [Fact]
    public void RootUsername_ShouldBeRootuser()
        // Assert
        => GlobalConstant.RootUsername.Should().Be("rootuser");

    [Fact]
    public void SystemUsername_ShouldBeSystem()
        // Assert
        => GlobalConstant.SystemUsername.Should().Be("System");

    [Fact]
    public void AutoIncrementStart_ShouldBe21()
        // Assert
        => GlobalConstant.AutoIncrementStart.Should().Be(21);

    [Fact]
    public void UserTypeClaimName_ShouldBeUt()
        // Assert
        => GlobalConstant.UserTypeClaimName.Should().Be("ut");

    [Fact]
    public void GenerateMetadataHeaderKey_ShouldBeMMetadata()
        // Assert
        => GlobalConstant.GenerateMetadataHeaderKey.Should().Be("M-Metadata");

    [Fact]
    public void RealIpHeaderKey_ShouldBeXRealIP()
        // Assert
        => GlobalConstant.RealIpHeaderKey.Should().Be("X-Real-IP");

    [Fact]
    public void DefaultIp_ShouldBeCorrect()
        // Assert
        => GlobalConstant.DefaultIp.Should().Be("0.0.0.1");

    [Fact]
    public void RootPath_ShouldNotBeNullOrEmpty()
        // Assert
        => GlobalConstant.RootPath.Should().NotBeNullOrEmpty();

    [Fact]
    public void SqlFilesPath_ShouldContainSqlFolder()
        // Assert
        => GlobalConstant.SqlFilesPath.Should().Contain("SQL");

    [Fact]
    public void JsonFilesPath_ShouldContainJsonFolder()
        // Assert
        => GlobalConstant.JsonFilesPath.Should().Contain("JSON");

    [Fact]
    public void ContentDispositionIgnores_ShouldContainExpectedValues()
    {
        // Assert
        GlobalConstant.ContentDispositionIgnores.Should().Contain("attachment");
        GlobalConstant.ContentDispositionIgnores.Should().Contain("inline");
    }

    [Fact]
    public void UIPaths_ShouldContainDocumentation()
    {
        // Assert
        GlobalConstant.UIPaths.Should().Contain("/api/documentation");
        GlobalConstant.UIPaths.Should().Contain("/api/docs");
    }

    [Fact]
    public void IgnoringLogPaths_ShouldContainHealthcheck()
        // Assert
        => GlobalConstant.IgnoringLogPaths.Should().Contain("healthcheck");

    [Fact]
    public void ActivitySource_ShouldNotBeNull()
    {
        // Assert
        GlobalConstant.ActivitySource.Should().NotBeNull();
        GlobalConstant.ActivitySource.Name.Should().Be("milvaion-api");
    }
}
