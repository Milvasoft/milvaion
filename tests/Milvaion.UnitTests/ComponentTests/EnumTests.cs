using FluentAssertions;
using Milvaion.Application.Features.ContentManagement.Contents.GetContent;
using Milvaion.Domain.Enums;
using System.ComponentModel;

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
    [InlineData(UserType.Manager, "Manager user type")]
    [InlineData(UserType.AppUser, "Application user type")]
    public void UserType_DescriptionAttribute_ShouldMatch(UserType userType, string expectedDescription)
    {
        // Arrange
        var type = typeof(UserType);
        var memberInfo = type.GetMember(userType.ToString());
        var attributes = memberInfo[0].GetCustomAttributes(typeof(DescriptionAttribute), false);
        var descriptionAttribute = (DescriptionAttribute)attributes[0];

        // Act
        var description = descriptionAttribute.Description;

        // Assert
        description.Should().Be(expectedDescription);
    }

    [Theory]
    [InlineData(UserType.Manager, 1)]
    [InlineData(UserType.AppUser, 2)]
    public void UserType_Value_ShouldMatch(UserType type, byte expectedValue)
    {
        // Act
        var value = (byte)type;

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
    [InlineData(NotificationType.OrderApproved, 0)]
    [InlineData(NotificationType.JobAutoDisabled, 1)]
    [InlineData(NotificationType.JobReEnabled, 2)]
    public void NotificationType_Value_ShouldMatch(NotificationType type, byte expectedValue)
    {
        // Act
        var value = (byte)type;

        // Assert
        value.Should().Be(expectedValue);
    }

    [Fact]
    public void NotificationType_ShouldHaveThreeValues()
    {
        // Act
        var values = Enum.GetValues<NotificationType>();

        // Assert
        values.Should().HaveCount(3);
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
        values.Should().HaveCount(22);
    }

    [Fact]
    public void UserType_ShouldHaveTwoValues()
    {
        // Act
        var values = Enum.GetValues<UserType>();

        // Assert
        values.Should().HaveCount(2);
    }

    [Fact]
    public void ContentQueryType_ShouldHaveThreeValues()
    {
        // Act
        var values = Enum.GetValues<ContentQueryType>();

        // Assert
        values.Should().HaveCount(3);
    }
}