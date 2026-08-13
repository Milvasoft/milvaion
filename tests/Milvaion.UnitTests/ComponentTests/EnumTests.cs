using FluentAssertions;
using Milvaion.Application.Features.ContentManagement.Contents.GetContent;
using Milvaion.Domain.Enums;

namespace Milvaion.UnitTests.ComponentTests;

[Trait("Enum Unit Tests", "Enums unit tests.")]
public class EnumTests
{
    [Theory]
    [InlineData(UserActivity.CreateUser, 0)]
    [InlineData(UserActivity.UpdateUser, 1)]
    [InlineData(UserActivity.DeleteUser, 2)]
    [InlineData(UserActivity.CreateRole, 3)]
    [InlineData(UserActivity.UpdateRole, 4)]
    [InlineData(UserActivity.DeleteRole, 5)]
    [InlineData(UserActivity.CreateNamespace, 6)]
    [InlineData(UserActivity.UpdateNamespace, 7)]
    [InlineData(UserActivity.DeleteNamespace, 8)]
    [InlineData(UserActivity.CreateResourceGroup, 9)]
    [InlineData(UserActivity.UpdateResourceGroup, 10)]
    [InlineData(UserActivity.DeleteResourceGroup, 11)]
    [InlineData(UserActivity.CreateContent, 12)]
    [InlineData(UserActivity.UpdateContent, 13)]
    [InlineData(UserActivity.DeleteContent, 14)]
    [InlineData(UserActivity.UpdateLanguages, 15)]
    public void UserActivity_Value_ShouldMatch(UserActivity activity, byte expectedValue)
    {
        // Act
        var value = (byte)activity;

        // Assert
        value.Should().Be(expectedValue);
    }

    [Theory]
    [InlineData(ContentQueryType.Key, 0)]
    [InlineData(ContentQueryType.ResourceGroup, 1)]
    [InlineData(ContentQueryType.Namespace, 2)]
    public void ContentQueryType_Value_ShouldMatch(ContentQueryType type, byte expectedValue)
    {
        // Act
        var value = (byte)type;

        // Assert
        value.Should().Be(expectedValue);
    }

    [Theory]
    [InlineData(AlertType.JobAutoDisabled, 4)]
    public void NotificationType_Value_ShouldMatch(AlertType type, byte expectedValue)
    {
        // Act
        var value = (byte)type;

        // Assert
        value.Should().Be(expectedValue);
    }

    [Fact]
    public void AlertType_ShouldHaveCorrectCountValues()
    {
        // Act
        var values = Enum.GetValues<AlertType>();

        // Assert
        values.Should().HaveCount(17);
    }

    [Theory]
    [InlineData(NotificationEntity.None, 0)]
    public void NotificationEntity_Value_ShouldMatch(NotificationEntity entity, byte expectedValue)
    {
        // Act
        var value = (byte)entity;

        // Assert
        value.Should().Be(expectedValue);
    }

    [Fact]
    public void NotificationEntity_ShouldHaveOneValue()
    {
        // Act
        var values = Enum.GetValues<NotificationEntity>();

        // Assert
        values.Should().HaveCount(1);
    }

    [Fact]
    public void UserActivity_ShouldHaveCorrectCount()
    {
        // Act
        var values = Enum.GetValues<UserActivity>();

        // Assert
        values.Should().HaveCount(33);
    }

    [Fact]
    public void ContentQueryType_ShouldHaveThreeValues()
    {
        // Act
        var values = Enum.GetValues<ContentQueryType>();

        // Assert
        values.Should().HaveCount(3);
    }

    [Theory]
    [InlineData(ExternalProvider.Local, 0)]
    [InlineData(ExternalProvider.Oidc, 1)]
    [InlineData(ExternalProvider.Ldap, 2)]
    public void ExternalProvider_Value_ShouldMatch(ExternalProvider provider, byte expectedValue)
    {
        // Act
        var value = (byte)provider;

        // Assert
        value.Should().Be(expectedValue);
    }

    [Fact]
    public void ExternalProvider_ShouldHaveThreeValues()
    {
        // Act
        var values = Enum.GetValues<ExternalProvider>();

        // Assert
        values.Should().HaveCount(3);
    }
}