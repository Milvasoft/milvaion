using FluentAssertions;
using Milvaion.Application.Utils.Constants;

namespace Milvaion.UnitTests.UtilsTests;

[Trait("Utils Unit Tests", "Constants unit tests.")]
public class ConstantsTests
{
    private static readonly string[] _expectedUIPaths =
    [
        "/api/documentation",
        "/api/docs"
    ];

    private static readonly string[] _expectedContentDispositionIgnores = ["attachment", "inline"];

    #region GlobalConstant

    [Fact]
    public void GlobalConstant_PropertyGetterSetter_ShouldWorkCorrectly()
    {
        // Arrange
        var currentEnvironment = "Development";

        // Act
        GlobalConstant.CurrentEnvironment = currentEnvironment;

        // Assert
        GlobalConstant.RoutePrefix.Should().Be("api");
        GlobalConstant.RouteBase.Should().Be("api/v{version:apiVersion}");
        GlobalConstant.FullRoute.Should().Be("api/v{version:apiVersion}/[controller]");
        GlobalConstant.WWWRoot.Should().Be("wwwroot");
        GlobalConstant.Http.Should().Be("http");
        GlobalConstant.Https.Should().Be("https");
        GlobalConstant.LocalizationResourcesFolderName.Should().Be("LocalizationResources");
        GlobalConstant.ResourcesFolderName.Should().Be("Resources");
        GlobalConstant.RootPath.Should().Be(Environment.CurrentDirectory);
        GlobalConstant.SqlFilesPath.Should().Be(Path.Combine(Environment.CurrentDirectory, "StaticFiles", "SQL"));
        GlobalConstant.DefaultApiVersion.Should().Be("v1.0");
        GlobalConstant.CustomForbidSchema.Should().Be("CustomForbidSchema");
        GlobalConstant.LoggingActivityName.Should().Be("LoggingActivity");
        GlobalConstant.CurrentEnvironment.Should().Be(currentEnvironment);
        GlobalConstant.UserTypeClaimName.Should().Be("ut");
        GlobalConstant.IgnoreResponseLoggingItemsKey.Should().Be("IgnoreResponseLogging");
        GlobalConstant.RootUsername.Should().Be("rootuser");
        GlobalConstant.GenerateMetadataHeaderKey.Should().Be("M-Metadata");
        GlobalConstant.ContentDispositionHeaderKey.Should().Be("Content-Disposition");
        GlobalConstant.RealIpHeaderKey.Should().Be("X-Real-IP");
        GlobalConstant.DownloadEnpointPathEnd.Should().Be("/download");
        GlobalConstant.DefaultIp.Should().Be("0.0.0.1");
        GlobalConstant.UrlStartSegment.Should().Be("://");
        GlobalConstant.ContentDispositionIgnores.Should().Contain(_expectedContentDispositionIgnores);
        GlobalConstant.UIPaths.Should().Contain(_expectedUIPaths);
    }

    #endregion

    #region LogTemplate

    [Fact]
    public void RequestResponse_Constant_ShouldHaveCorrectValue()
    {
        // Arrange
        var expectedValue = "{TransactionId}{Severity}{Timestamp}{Path}{@RequestInfoJson}{@ResponseInfoJson}{ElapsedMs}{IpAddress}{UserName}{@Exception}";

        // Act
        var actualValue = LogTemplate.RequestResponse;

        // Assert
        actualValue.Should().Be(expectedValue);
    }

    #endregion

    #region MessageConstant

    [Fact]
    public void MessageConstant_Properties_ShouldHaveCorrectValues()
    {
        // Assert
        MessageConstant.CurlyBracket.Should().Be('{');
        MessageConstant.Filtered.Should().Be("FILTERED");
        MessageConstant.QuestionMark.Should().Be("?");
        MessageConstant.Hypen.Should().Be("-");
        MessageConstant.NoException.Should().Be("No exception.");
        MessageConstant.DefaultDataCannotModifyPgCode.Should().Be("P0001");
        MessageConstant.DefaultDataCannotModifyPgMessage.Should().Be("Seed records cannot be");
        MessageConstant.DuplicateDataViolationPgCode.Should().Be("23505");
        MessageConstant.ForeignKeyViolationPgCode.Should().Be("20503");
        MessageConstant.ExceptionLogTemplate.Should().Be("Exception raised : {message}");
        MessageConstant.S3ConfigurationMissing.Should().Be("Current provider type is S3 but S3 configuration missing!");
        MessageConstant.ABSConfigurationMissing.Should().Be("Current provider type is ABS but ABS configuration missing!");
        MessageConstant.OpenApiAuthMessageTip.Should().Be(
            """
                JWT Authorization header using the Bearer scheme.
                Enter your token in the text input below without Bearer. Bearer will automatically be added.
                Example: '12345abcdef'
            """);
    }

    #endregion

    #region MessageKey

    [Fact]
    public void MessageKey_Constants_ShouldHaveCorrectValues()
    {
        // Assert
        MessageKey.SlugExplaniton.Should().Be(nameof(MessageKey.SlugExplaniton));
        MessageKey.InvalidParameterException.Should().Be(nameof(MessageKey.InvalidParameterException));
        MessageKey.UnhandledException.Should().Be(nameof(MessageKey.UnhandledException));
        MessageKey.PostgreBasedException.Should().Be(nameof(MessageKey.PostgreBasedException));
        MessageKey.Unauthorized.Should().Be(nameof(MessageKey.Unauthorized));
        MessageKey.Forbidden.Should().Be(nameof(MessageKey.Forbidden));
        MessageKey.SessionTimeout.Should().Be("SessionTimeout");
        MessageKey.UserExists.Should().Be("User.ExistWithSameUsername");
        MessageKey.UserNotFound.Should().Be("User.NotFound");
        MessageKey.RoleNotFound.Should().Be("Role.NotFound");
        MessageKey.WrongPassword.Should().Be("WrongPassword");
        MessageKey.DefaultValueCannotModify.Should().Be(nameof(MessageKey.DefaultValueCannotModify));
        MessageKey.PleaseSendCorrect.Should().Be(nameof(MessageKey.PleaseSendCorrect));
        MessageKey.IdentityInvalidUsername.Should().Be(nameof(MessageKey.IdentityInvalidUsername));
        MessageKey.CannotBeEmpty.Should().Be(nameof(MessageKey.CannotBeEmpty));
        MessageKey.PossibleUIError.Should().Be("PossibleUIError");
        MessageKey.User.Should().Be("Global.User");
        MessageKey.Role.Should().Be("Global.Role");
        MessageKey.Page.Should().Be("Global.Page");
        MessageKey.PageNotFound.Should().Be("Page.NotFound");
        MessageKey.PostgreDuplicateDataException.Should().Be(nameof(MessageKey.PostgreDuplicateDataException));
        MessageKey.Hours.Should().Be(nameof(MessageKey.Hours));
        MessageKey.Minutes.Should().Be(nameof(MessageKey.Minutes));
        MessageKey.Seconds.Should().Be(nameof(MessageKey.Seconds));
        MessageKey.Locked.Should().Be(nameof(MessageKey.Locked));
        MessageKey.LockWarning.Should().Be(nameof(MessageKey.LockWarning));
        MessageKey.NamespaceNotFound.Should().Be(nameof(MessageKey.NamespaceNotFound));
        MessageKey.ResourceGroupNotFound.Should().Be(nameof(MessageKey.ResourceGroupNotFound));
        MessageKey.ContentNotFound.Should().Be(nameof(MessageKey.ContentNotFound));
        MessageKey.MediaNotFound.Should().Be(nameof(MessageKey.MediaNotFound));
        MessageKey.Namespace.Should().Be("Global.Namespace");
        MessageKey.ResourceGroup.Should().Be("Global.ResourceGroup");
        MessageKey.Content.Should().Be("Global.Content");
        MessageKey.Media.Should().Be("Global.Media");
        MessageKey.Language.Should().Be("Global.Language");
        MessageKey.UnsupportedMediaType.Should().Be(nameof(MessageKey.UnsupportedMediaType));
        MessageKey.OnlyImageFilesAccepted.Should().Be(nameof(MessageKey.OnlyImageFilesAccepted));
        MessageKey.FileSizeMustLowerThan.Should().Be(nameof(MessageKey.FileSizeMustLowerThan));
        MessageKey.Query.Should().Be(nameof(MessageKey.Query));
        MessageKey.QueryType.Should().Be(nameof(MessageKey.QueryType));
    }

    #endregion
}
