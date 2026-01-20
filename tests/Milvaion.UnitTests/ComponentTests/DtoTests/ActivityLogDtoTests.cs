using FluentAssertions;
using Microsoft.EntityFrameworkCore.Query;
using Milvaion.Application.Dtos.ActivityLogDtos;
using Milvaion.Domain;
using Milvaion.Domain.Enums;
using System.Linq.Expressions;

namespace Milvaion.UnitTests.ComponentTests.DtoTests;

[Trait("DTO Unit Tests", "DTO models property and method unit tests.")]
public class ActivityLogDtoTests
{
    [Fact]
    public void ActivityLogListDto_PropertyGetterSetter_ShouldWorkCorrectly()
    {
        // Arrange
        var id = 1;
        var userName = "TestUser";
        var activity = UserActivity.CreateUser;
        var activityDescription = "User added";
        var activityDate = DateTimeOffset.Now;

        // Act
        var activityLogListDto = new ActivityLogListDto
        {
            Id = id,
            UserName = userName,
            Activity = activity,
            ActivityDescription = activityDescription,
            ActivityDate = activityDate,
        };

        // Assert
        activityLogListDto.Id.Should().Be(id);
        activityLogListDto.UserName.Should().Be(userName);
        activityLogListDto.Activity.Should().Be(activity);
        activityLogListDto.ActivityDescription.Should().Be(activityDescription);
        activityLogListDto.ActivityDate.Should().Be(activityDate);
    }

    [Fact]
    public void ActivityLogListDto_Projection_ShouldReturnCorrectly()
    {
        // Arrange
        Expression<Func<ActivityLog, ActivityLogListDto>> expectedExpression = a => new ActivityLogListDto
        {
            Id = a.Id,
            UserName = a.UserName,
            Activity = a.Activity,
            ActivityDate = a.ActivityDate,
        };

        // Act
        var result = ActivityLogListDto.Projection;

        // Assert
        var equality = ExpressionEqualityComparer.Instance.Equals(expectedExpression, result);

        equality.Should().BeTrue();
    }
}
