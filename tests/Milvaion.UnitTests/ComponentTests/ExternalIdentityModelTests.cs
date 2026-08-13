using FluentAssertions;
using Milvaion.Application.Interfaces;
using Milvaion.Domain.Enums;

namespace Milvaion.UnitTests.ComponentTests;

[Trait("External Identity Unit Tests", "Unit tests for external identity model records.")]
public class ExternalIdentityModelTests
{
    [Fact]
    public void LdapAuthResult_Fail_ShouldReturnUnsuccessfulResult()
    {
        // Act
        var result = LdapAuthResult.Fail();

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeFalse();
        result.Subject.Should().BeNull();
        result.Email.Should().BeNull();
        result.Name.Should().BeNull();
        result.Surname.Should().BeNull();
        result.Groups.Should().NotBeNull();
        result.Groups.Should().BeEmpty();
    }

    [Fact]
    public void LdapAuthResult_PropertyGetterSetter_ShouldWorkCorrectly()
    {
        // Act
        var result = new LdapAuthResult
        {
            Success = true,
            Subject = "subject",
            Email = "user@example.com",
            Name = "Name",
            Surname = "Surname",
            Groups = ["Admins", "Users"]
        };

        // Assert
        result.Success.Should().BeTrue();
        result.Subject.Should().Be("subject");
        result.Email.Should().Be("user@example.com");
        result.Name.Should().Be("Name");
        result.Surname.Should().Be("Surname");
        result.Groups.Should().BeEquivalentTo("Admins", "Users");
    }

    [Fact]
    public void ExternalIdentityDescriptor_ShouldDefaultToEmptyRoleNames()
    {
        // Act
        var descriptor = new ExternalIdentityDescriptor();

        // Assert
        descriptor.Provider.Should().Be(ExternalProvider.Local);
        descriptor.RoleNames.Should().NotBeNull();
        descriptor.RoleNames.Should().BeEmpty();
    }

    [Fact]
    public void ExternalIdentityDescriptor_PropertyGetterSetter_ShouldWorkCorrectly()
    {
        // Act
        var descriptor = new ExternalIdentityDescriptor
        {
            Provider = ExternalProvider.Ldap,
            Issuer = "ldap.example.com",
            Subject = "subject",
            UserName = "jdoe",
            Email = "jdoe@example.com",
            Name = "John",
            Surname = "Doe",
            RoleNames = ["Admins"]
        };

        // Assert
        descriptor.Provider.Should().Be(ExternalProvider.Ldap);
        descriptor.Issuer.Should().Be("ldap.example.com");
        descriptor.Subject.Should().Be("subject");
        descriptor.UserName.Should().Be("jdoe");
        descriptor.Email.Should().Be("jdoe@example.com");
        descriptor.Name.Should().Be("John");
        descriptor.Surname.Should().Be("Doe");
        descriptor.RoleNames.Should().BeEquivalentTo("Admins");
    }
}
