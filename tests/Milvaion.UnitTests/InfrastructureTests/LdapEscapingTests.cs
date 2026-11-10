using FluentAssertions;
using Milvaion.Infrastructure.Services;

namespace Milvaion.UnitTests.InfrastructureTests;

/// <summary>
/// Unit tests for the escaping of user input placed into LDAP filters and distinguished names.
/// </summary>
public class LdapEscapingTests
{
    [Theory]
    [InlineData("jdoe", "jdoe")]
    [InlineData("x)(cn=admin", @"x\29\28cn=admin")]
    [InlineData("*", @"\2a")]
    [InlineData(@"a\b", @"a\5cb")]
    public void EscapeFilterValue_ShouldEscapeFilterMetacharacters(string input, string expected)
        => LdapAuthenticator.EscapeFilterValue(input).Should().Be(expected);

    [Theory]
    [InlineData("jdoe", "jdoe")]
    [InlineData("a,ou=admins", @"a\,ou\=admins")]
    [InlineData(" jdoe ", @"\ jdoe\ ")]
    [InlineData("#jdoe", @"\#jdoe")]
    public void EscapeDnValue_ShouldEscapeDnMetacharacters(string input, string expected)
        => LdapAuthenticator.EscapeDnValue(input).Should().Be(expected);
}
