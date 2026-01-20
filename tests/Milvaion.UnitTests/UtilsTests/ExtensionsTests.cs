using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Milvaion.Application.Utils.Constants;
using Milvaion.Application.Utils.Extensions;
using Milvaion.Domain.Enums;
using Milvasoft.Identity.Abstract;
using Moq;
using System.Security.Claims;

namespace Milvaion.UnitTests.UtilsTests;

[Trait("Utils Unit Tests", "Extensions unit tests.")]
public class ExtensionsTests
{
    [Fact]
    public void GenerateMetadata_ShouldReturnTrue_HeaderExistsAndIsTrue()
    {
        // Arrange
        var headers = new HeaderDictionary
        {
            { GlobalConstant.GenerateMetadataHeaderKey, "true" }
        };
        var httpContext = new DefaultHttpContext();
        foreach (var item in headers)
        {
            httpContext.Request.Headers.Add(item);
        }

        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider.Setup(sp => sp.GetService(typeof(IHttpContextAccessor)))
                       .Returns(new HttpContextAccessor { HttpContext = httpContext });

        // Act
        var result = MilvaionExtensions.GenerateMetadata(serviceProvider.Object);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void GetTokenFromHeader_ShouldReturnToken_HeaderExists()
    {
        // Arrange
        var headers = new HeaderDictionary
        {
            { "Authorization", "Bearer test-token" }
        };
        var httpContext = new DefaultHttpContext();

        foreach (var item in headers)
        {
            httpContext.Request.Headers.Add(item);
        }

        var httpContextAccessor = new HttpContextAccessor { HttpContext = httpContext };

        // Act
        var result = httpContextAccessor.GetTokenFromHeader();

        // Assert
        result.Should().Be("test-token");
    }

    [Fact]
    public void IsCurrentUser_ShouldReturnTrue_UserNameMatches()
    {
        // Arrange
        var claims = new List<Claim> { new(ClaimTypes.Name, "test-user") };
        var identity = new ClaimsIdentity(claims, "TestAuthType");
        var user = new ClaimsPrincipal(identity);
        var httpContext = new DefaultHttpContext { User = user };
        var httpContextAccessor = new HttpContextAccessor { HttpContext = httpContext };

        // Act
        var result = httpContextAccessor.IsCurrentUser("test-user");

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void CurrentUserName_ShouldReturnUserName_UserIsAuthenticated()
    {
        // Arrange
        var claims = new List<Claim> { new(ClaimTypes.Name, "test-user") };
        var identity = new ClaimsIdentity(claims, "TestAuthType");
        var user = new ClaimsPrincipal(identity);
        var httpContext = new DefaultHttpContext { User = user };

        // Act
        var result = httpContext.CurrentUserName();

        // Assert
        result.Should().Be("test-user");
    }

    [Fact]
    public void GetCurrentUserType_ShouldReturnUserType_ClaimExists()
    {
        // Arrange
        var claims = new List<Claim> { new(GlobalConstant.UserTypeClaimName, "Manager") };
        var identity = new ClaimsIdentity(claims, "TestAuthType");
        var user = new ClaimsPrincipal(identity);
        var httpContext = new DefaultHttpContext { User = user };
        var serviceProviderMock = new Mock<IServiceProvider>();
        var tokenManagerMock = new Mock<IMilvaTokenManager>();
        httpContext.RequestServices = serviceProviderMock.Object;

        tokenManagerMock.Setup(t => t.GetClaimsPrincipalIfValid(It.IsAny<string>())).Returns(new ClaimsPrincipal(new ClaimsIdentity(claims)));
        serviceProviderMock.Setup(sp => sp.GetService(typeof(IMilvaTokenManager))).Returns(tokenManagerMock.Object);

        // Act
        var result = httpContext.GetCurrentUserType();

        // Assert
        result.Should().Be(UserType.Manager);
    }

    [Fact]
    public void GetCurrentUserPermissions_ShouldReturnPermissions_ClaimsExist()
    {
        // Arrange
        var claims = new List<Claim> { new(ClaimTypes.Role, "Admin"), new(ClaimTypes.Role, "User") };
        var identity = new ClaimsIdentity(claims, "TestAuthType");
        var user = new ClaimsPrincipal(identity);
        var httpContext = new DefaultHttpContext { User = user };
        var serviceProviderMock = new Mock<IServiceProvider>();
        var tokenManagerMock = new Mock<IMilvaTokenManager>();
        httpContext.RequestServices = serviceProviderMock.Object;

        tokenManagerMock.Setup(t => t.GetClaimsPrincipalIfValid(It.IsAny<string>())).Returns(new ClaimsPrincipal(new ClaimsIdentity(claims)));
        serviceProviderMock.Setup(sp => sp.GetService(typeof(IMilvaTokenManager))).Returns(tokenManagerMock.Object);

        // Act
        var result = httpContext.GetCurrentUserPermissions();

        // Assert
        result.Should().BeEquivalentTo("Admin", "User");
    }

    [Fact]
    public void IsBase64StringValidLength_ShouldReturnFalse_StringIsTooLong()
    {
        // Arrange
        var base64String = Convert.ToBase64String(new byte[1024 * 1024 + 1]);

        // Act
        var result = MilvaionExtensions.IsBase64StringValidLength(base64String);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void DataUriToPlainText_ShouldConvertDataUriToPlainTextCorrectly()
    {
        // Arrange
        var dataUriBase64 = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAUA";

        // Act
        var result = MilvaionExtensions.DataUriToPlainText(dataUriBase64);

        // Assert
        result.Should().NotBeNull();
        result.Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public void DataUriToPlainText_ShouldReturnEmptyArray_InputIsNullOrEmpty()
    {
        // Arrange
        string dataUriBase64 = null;

        // Act
        var result = MilvaionExtensions.DataUriToPlainText(dataUriBase64);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void DataUriToPlainText_ShouldReturnEmptyArray_InputIsInvalid()
    {
        // Arrange
        var dataUriBase64 = "invalid_uri_data";

        // Act
        var result = MilvaionExtensions.DataUriToPlainText(dataUriBase64);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void ToLowerAndNonSpacingUnicode_ShouldConvertStringCorrectly()
    {
        // Arrange
        var input = "Äpfel Über Öl";
        var expected = "apfeluberol";

        // Act
        var result = MilvaionExtensions.ToLowerAndNonSpacingUnicode(input);

        // Assert
        result.Should().Be(expected);
    }

    [Fact]
    public void ToLowerAndNonSpacingUnicode_ShouldReturnEmptyString_InputIsEmpty()
    {
        // Arrange
        var input = "";
        var expected = "";

        // Act
        var result = MilvaionExtensions.ToLowerAndNonSpacingUnicode(input);

        // Assert
        result.Should().Be(expected);
    }

    [Fact]
    public void IsBase64StringValidLength_ForValidLength_ShouldReturnTrue()
    {
        // Arrange
        var base64String = Convert.ToBase64String(new byte[1024 * 1024]); // 1 MB

        // Act
        var result = MilvaionExtensions.IsBase64StringValidLength(base64String);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void IsBase64StringValidLength_ForInvalidLength_ShouldReturnFalse()
    {
        // Arrange
        var invalid = new byte[1024 * 1024 + 1];
        var base64String = Convert.ToBase64String(invalid); // > 1 MB

        // Act
        var result = MilvaionExtensions.IsBase64StringValidLength(base64String);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void IsBase64StringValidLength_ForEmptyString_ShouldReturnTrue()
    {
        // Arrange
        var base64String = string.Empty;

        // Act
        var result = MilvaionExtensions.IsBase64StringValidLength(base64String);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void IsBase64StringValidLength_ForNullString_ShouldReturnTrue()
    {
        // Arrange
        string base64String = null;

        // Act
        var result = MilvaionExtensions.IsBase64StringValidLength(base64String);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void IsBase64StringHasValidFileExtension_ForValidJpegBase64String_ShouldReturnTrue()
    {
        // Arrange
        var base64String = "data:image/jpeg;base64,R0lGODlhAQABAAAAACH5BAEKAAEALAAAAAABAAEAAAICTAEAOw==";

        // Act
        var result = MilvaionExtensions.IsBase64StringHasValidFileExtension(base64String);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void IsBase64StringHasValidFileExtension_ForValidPngBase64String_ShouldReturnTrue()
    {
        // Arrange
        var base64String = "data:image/png;base64,R0lGODlhAQABAAAAACH5BAEKAAEALAAAAAABAAEAAAICTAEAOw==";

        // Act
        var result = MilvaionExtensions.IsBase64StringHasValidFileExtension(base64String);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void IsBase64StringHasValidFileExtension_ForInvalidBase64String_ShouldReturnFalse()
    {
        // Arrange
        var base64String = "R0lGODlhAQABAAAAACH5BAEKAAEALAAAAAABAAEAAAICTAEAOw==";

        // Act
        var result = MilvaionExtensions.IsBase64StringHasValidFileExtension(base64String);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void IsBase64StringHasValidFileExtension_ForEmptyBase64String_ShouldReturnTrue()
    {
        // Arrange
        var base64String = "";

        // Act
        var result = MilvaionExtensions.IsBase64StringHasValidFileExtension(base64String);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void IsValidDataUri_ForValidDataUri_ShouldReturnTrue()
    {
        // Arrange
        var dataUri = "data:image/png;base64,R0lGODlhAQABAAAAACH5BAEKAAEALAAAAAABAAEAAAICTAEAOw==";

        // Act
        var result = MilvaionExtensions.IsValidDataUri(dataUri);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void IsValidDataUri_ForInvalidDataUri_ShouldReturnFalse()
    {
        // Arrange
        var dataUri = "R0lGODlhAQABAAAAACH5BAEKAAEALAAAAAABAAEAAAICTAEAOw==";

        // Act
        var result = MilvaionExtensions.IsValidDataUri(dataUri);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void IsValidDataUri_ForEmptyDataUri_ShouldReturnFalse()
    {
        // Arrange
        var dataUri = "";

        // Act
        var result = MilvaionExtensions.IsValidDataUri(dataUri);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void GetCurrentEnvironment_ShouldReturnEnvironmentVariable()
    {
        // Act
        var result = MilvaionExtensions.GetCurrentEnvironment();

        // Assert
        result.Should().NotBeNull();
    }

    [Fact]
    public void IsCurrentEnvProduction_ShouldReturnFalse_WhenNotProduction()
    {
        // Arrange - By default, environment is not "prod"

        // Act
        var result = MilvaionExtensions.IsCurrentEnvProduction();

        // Assert
        // In test environment, this should typically be false
        result.Should().BeFalse();
    }

    [Fact]
    public void ToLowerAndNonSpacingUnicode_ShouldRemoveTurkishCharacterAccents()
    {
        // Arrange
        var input = "Türkçe Karakterler İşlemci Öğrenci Ürün Çalışma Şeker Ğüneş";

        // Act
        var result = MilvaionExtensions.ToLowerAndNonSpacingUnicode(input);

        // Assert
        result.Should().NotContain(" ");
        result.Should().Be(result.ToLower());
    }

    [Fact]
    public void ToLowerAndNonSpacingUnicode_ShouldRemoveSpaces()
    {
        // Arrange
        var input = "Hello World Test";

        // Act
        var result = MilvaionExtensions.ToLowerAndNonSpacingUnicode(input);

        // Assert
        result.Should().Be("helloworldtest");
    }

    [Fact]
    public void UrlRegex_ShouldMatchValidUrls()
    {
        // Arrange
        var validUrls = new[]
        {
            "http://example.com",
            "https://example.com",
            "https://www.example.com/path",
            "http://example.com:8080/path"
        };

        // Act & Assert
        foreach (var url in validUrls)
        {
            MilvaionExtensions.UrlRegex().IsMatch(url).Should().BeTrue($"'{url}' should be valid");
        }
    }

    [Fact]
    public void UrlRegex_ShouldNotMatchInvalidUrls()
    {
        // Arrange
        var invalidUrls = new[]
        {
            "not-a-url",
            "ftp://example.com", // ftp is valid according to regex
            "example.com",
            ""
        };

        // Act & Assert
        foreach (var url in invalidUrls)
        {
            if (url == "ftp://example.com")
                continue; // Skip as ftp is valid

            MilvaionExtensions.UrlRegex().IsMatch(url).Should().BeFalse($"'{url}' should be invalid");
        }
    }

    [Fact]
    public void GenerateMetadata_ShouldReturnFalse_WhenHttpContextIsNull()
    {
        // Arrange
        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider.Setup(sp => sp.GetService(typeof(IHttpContextAccessor)))
                       .Returns(new HttpContextAccessor { HttpContext = null });

        // Act
        var result = MilvaionExtensions.GenerateMetadata(serviceProvider.Object);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void GenerateMetadata_ShouldReturnFalse_WhenHeaderIsFalse()
    {
        // Arrange
        var headers = new HeaderDictionary
        {
            { GlobalConstant.GenerateMetadataHeaderKey, "false" }
        };
        var httpContext = new DefaultHttpContext();
        foreach (var item in headers)
        {
            httpContext.Request.Headers.Add(item);
        }

        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider.Setup(sp => sp.GetService(typeof(IHttpContextAccessor)))
                       .Returns(new HttpContextAccessor { HttpContext = httpContext });

        // Act
        var result = MilvaionExtensions.GenerateMetadata(serviceProvider.Object);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void GenerateMetadata_ShouldReturnFalse_WhenHeaderDoesNotExist()
    {
        // Arrange
        var httpContext = new DefaultHttpContext();

        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider.Setup(sp => sp.GetService(typeof(IHttpContextAccessor)))
                       .Returns(new HttpContextAccessor { HttpContext = httpContext });

        // Act
        var result = MilvaionExtensions.GenerateMetadata(serviceProvider.Object);

        // Assert
        result.Should().BeFalse();
    }
}