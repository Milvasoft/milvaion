using FluentAssertions;
using Milvaion.Application.Dtos;
using Milvaion.Domain.ContentManagement;

namespace Milvaion.UnitTests.ComponentTests.DtoTests;

[Trait("DTO Unit Tests", "DTO models property and method unit tests.")]
public class CommonDtoTests
{
    [Fact]
    public void AuditDto_PropertyGetterSetter_ShouldWorkCorrectly()
    {
        // Arrange
        var creationDate = DateTime.Now;
        var creatorUserName = "CreatorUser";
        var lastModificationDate = DateTime.Now.AddHours(1);
        var lastModifierUserName = "LastModifierUser";

        // Act
        var auditDto = new AuditDto<int>(new Content
        {
            LastModificationDate = lastModificationDate,
            LastModifierUserName = lastModifierUserName,
            CreationDate = creationDate,
            CreatorUserName = creatorUserName
        });

        // Assert
        auditDto.CreationDate.Should().Be(creationDate);
        auditDto.CreatorUserName.Should().Be(creatorUserName);
        auditDto.LastModificationDate.Should().Be(lastModificationDate);
        auditDto.LastModifierUserName.Should().Be(lastModifierUserName);
    }

    [Fact]
    public void EnumLookupModel_PropertyGetterSetter_ShouldWorkCorrectly()
    {
        // Arrange
        var value = new object();
        var name = "TestName";

        // Act
        var enumLookupModel = new EnumLookupModel
        {
            Value = value,
            Name = name
        };

        // Assert
        enumLookupModel.Value.Should().Be(value);
        enumLookupModel.Name.Should().Be(name);
    }

    [Fact]
    public void NameIntNavigationDto_PropertyGetterSetter_ShouldWorkCorrectly()
    {
        // Arrange
        var id = 1;
        var name = "TestName";

        // Act
        var dto = new NameIntNavigationDto
        {
            Id = id,
            Name = name
        };

        // Assert
        dto.Id.Should().Be(id);
        dto.Name.Should().Be(name);
    }
}
