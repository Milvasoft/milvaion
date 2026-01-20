using FluentAssertions;
using Microsoft.EntityFrameworkCore.Query;
using Milvaion.Application.Dtos.PermissionDtos;
using Milvaion.Domain;
using System.Linq.Expressions;

namespace Milvaion.UnitTests.ComponentTests.DtoTests;

[Trait("DTO Unit Tests", "DTO models property and method unit tests.")]
public class PermissionDtoTests
{
    [Fact]
    public void PermissionListDto_PropertyGetterSetter_ShouldWorkCorrectly()
    {
        // Arrange
        var id = 1;
        var name = "TestName";
        var description = "TestDescription";
        var permissionGroup = "TestPermissionGroup";
        var permissionGroupDescription = "TestPermissionGroupDescription";

        // Act
        var permissionListDto = new PermissionListDto
        {
            Id = id,
            Name = name,
            Description = description,
            PermissionGroup = permissionGroup,
            PermissionGroupDescription = permissionGroupDescription
        };

        // Assert
        permissionListDto.Id.Should().Be(id);
        permissionListDto.Name.Should().Be(name);
        permissionListDto.Description.Should().Be(description);
        permissionListDto.PermissionGroup.Should().Be(permissionGroup);
        permissionListDto.PermissionGroupDescription.Should().Be(permissionGroupDescription);
    }

    [Fact]
    public void PermissionListDto_Projection_ShouldReturnCorrectly()
    {
        // Arrange
        Expression<Func<Permission, PermissionListDto>> expectedExpression = p => new PermissionListDto
        {
            Id = p.Id,
            Name = p.Name,
            Description = p.Description,
            PermissionGroup = p.PermissionGroup,
            PermissionGroupDescription = p.PermissionGroupDescription
        };

        // Act
        var result = PermissionListDto.Projection;

        // Assert
        var equality = ExpressionEqualityComparer.Instance.Equals(expectedExpression, result);

        equality.Should().BeTrue();
    }
}
