using FluentAssertions;
using Milvaion.Application.Utils.Extensions;
using Milvasoft.Core.Utils.Constants;

namespace Milvaion.UnitTests.UtilsTests;

[Trait("Utils Unit Tests", "FileHelper and MimeTypeHelper unit tests.")]
public class FileHelperTests
{
    #region GetFileExtension Tests

    [Fact]
    public void GetFileExtension_ShouldReturnExtension_WhenPathHasExtension()
    {
        // Arrange
        var filePath = "document.pdf";

        // Act
        var result = FileHelper.GetFileExtension(filePath);

        // Assert
        result.Should().Be(".pdf");
    }

    [Fact]
    public void GetFileExtension_ShouldReturnEmpty_WhenPathHasNoExtension()
    {
        // Arrange
        var filePath = "document";

        // Act
        var result = FileHelper.GetFileExtension(filePath);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void GetFileExtension_ShouldReturnEmpty_WhenPathIsNull()
    {
        // Arrange
        string filePath = null;

        // Act
        var result = FileHelper.GetFileExtension(filePath);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void GetFileExtension_ShouldReturnEmpty_WhenPathIsEmpty()
    {
        // Arrange
        var filePath = string.Empty;

        // Act
        var result = FileHelper.GetFileExtension(filePath);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void GetFileExtension_ShouldHandleFullPath()
    {
        // Arrange
        var filePath = "/home/user/documents/report.xlsx";

        // Act
        var result = FileHelper.GetFileExtension(filePath);

        // Assert
        result.Should().Be(".xlsx");
    }

    #endregion

    #region GetFileNameWithoutExtension Tests

    [Fact]
    public void GetFileNameWithoutExtension_ShouldReturnFileName()
    {
        // Arrange
        var filePath = "document.pdf";

        // Act
        var result = FileHelper.GetFileNameWithoutExtension(filePath);

        // Assert
        result.Should().Be("document");
    }

    [Fact]
    public void GetFileNameWithoutExtension_ShouldReturnEmpty_WhenPathIsNull()
    {
        // Arrange
        string filePath = null;

        // Act
        var result = FileHelper.GetFileNameWithoutExtension(filePath);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void GetFileNameWithoutExtension_ShouldReturnEmpty_WhenPathIsEmpty()
    {
        // Arrange
        var filePath = string.Empty;

        // Act
        var result = FileHelper.GetFileNameWithoutExtension(filePath);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void GetFileNameWithoutExtension_ShouldHandleFullPath()
    {
        // Arrange
        var filePath = "/home/user/documents/report.xlsx";

        // Act
        var result = FileHelper.GetFileNameWithoutExtension(filePath);

        // Assert
        result.Should().Be("report");
    }

    #endregion

    #region GetFileName Tests

    [Fact]
    public void GetFileName_ShouldReturnFileNameWithExtension()
    {
        // Arrange
        var filePath = "/home/user/documents/report.xlsx";

        // Act
        var result = FileHelper.GetFileName(filePath);

        // Assert
        result.Should().Be("report.xlsx");
    }

    [Fact]
    public void GetFileName_ShouldReturnEmpty_WhenPathIsNull()
    {
        // Arrange
        string filePath = null;

        // Act
        var result = FileHelper.GetFileName(filePath);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void GetFileName_ShouldReturnEmpty_WhenPathIsEmpty()
    {
        // Arrange
        var filePath = string.Empty;

        // Act
        var result = FileHelper.GetFileName(filePath);

        // Assert
        result.Should().BeEmpty();
    }

    #endregion

    #region FileSignatures Tests

    [Fact]
    public void FileSignatures_ShouldContainApkSignature()
    {
        // Assert
        FileHelper.FileSignatures.Should().ContainKey(".apk");
        FileHelper.FileSignatures[".apk"].Should().NotBeEmpty();
    }

    [Fact]
    public void FileSignatures_ShouldContainIpaSignature()
    {
        // Assert
        FileHelper.FileSignatures.Should().ContainKey(".ipa");
        FileHelper.FileSignatures[".ipa"].Should().NotBeEmpty();
    }

    [Fact]
    public void ApkAndIpaSignatures_ShouldBeEqual()
    {
        // Both APK and IPA are ZIP-based formats with same signature
        var apkSig = FileHelper.FileSignatures[".apk"][0];
        var ipaSig = FileHelper.FileSignatures[".ipa"][0];

        apkSig.Should().BeEquivalentTo(ipaSig);
    }

    #endregion

    #region Constants Tests

    [Fact]
    public void IpaExtension_ShouldBeCorrect()
        // Assert
        => FileHelper.IpaExtension.Should().Be(".ipa");

    [Fact]
    public void ApkExtension_ShouldBeCorrect()
        // Assert
        => FileHelper.ApkExtension.Should().Be(".apk");

    #endregion
}

[Trait("Utils Unit Tests", "MimeTypeHelper unit tests.")]
public class MimeTypeHelperTests
{
    [Fact]
    public void ApplicationApk_ShouldBeCorrectMimeType()
        // Assert
        => FileHelper.MimeTypeHelper.ApplicationApk.Should().Be("application/vnd.android.package-archive");

    [Fact]
    public void ApplicationIpa_ShouldBeCorrectMimeType()
        // Assert
        => FileHelper.MimeTypeHelper.ApplicationIpa.Should().Be("application/x-itunes-ipa");

    [Fact]
    public void GetMimeType_ShouldReturnCorrectMimeType_ForCommonExtensions()
    {
        // Arrange & Act & Assert
        FileHelper.MimeTypeHelper.GetMimeType(".pdf").Should().Be("application/pdf");
        FileHelper.MimeTypeHelper.GetMimeType(".json").Should().Be("application/json");
        FileHelper.MimeTypeHelper.GetMimeType(".html").Should().Be("text/html");
        FileHelper.MimeTypeHelper.GetMimeType(".css").Should().Be("text/css");
        FileHelper.MimeTypeHelper.GetMimeType(".js").Should().Be("text/javascript");
    }

    [Fact]
    public void GetMimeType_ShouldReturnCorrectMimeType_ForImageExtensions()
    {
        // Arrange & Act & Assert
        FileHelper.MimeTypeHelper.GetMimeType(".png").Should().Be("image/png");
        FileHelper.MimeTypeHelper.GetMimeType(".jpg").Should().Be("image/jpeg");
        FileHelper.MimeTypeHelper.GetMimeType(".jpeg").Should().Be("image/jpeg");
        FileHelper.MimeTypeHelper.GetMimeType(".gif").Should().Be("image/gif");
        FileHelper.MimeTypeHelper.GetMimeType(".svg").Should().Be("image/svg+xml");
    }

    [Fact]
    public void GetMimeType_ShouldReturnOctetStream_ForUnknownExtension()
    {
        // Arrange
        var unknownExtension = ".unknownext123";

        // Act
        var result = FileHelper.MimeTypeHelper.GetMimeType(unknownExtension);

        // Assert
        result.Should().Be(MimeTypeNames.ApplicationOctetStream);
    }

    [Fact]
    public void GetMimeType_ShouldReturnCorrectMimeType_ForApk()
    {
        // Act
        var result = FileHelper.MimeTypeHelper.GetMimeType(".apk");

        // Assert
        result.Should().Be(FileHelper.MimeTypeHelper.ApplicationApk);
    }

    [Fact]
    public void GetMimeType_ShouldReturnCorrectMimeType_ForIpa()
    {
        // Act
        var result = FileHelper.MimeTypeHelper.GetMimeType(".ipa");

        // Assert
        result.Should().Be(FileHelper.MimeTypeHelper.ApplicationIpa);
    }

    [Fact]
    public void ExtensionMimeTypePairs_ShouldContainApkMapping()
        // Assert
        => FileHelper.MimeTypeHelper.ExtensionMimeTypePairs.Should().ContainKey(".apk");

    [Fact]
    public void ExtensionMimeTypePairs_ShouldContainIpaMapping()
        // Assert
        => FileHelper.MimeTypeHelper.ExtensionMimeTypePairs.Should().ContainKey(".ipa");

    [Fact]
    public void ExtensionMimeTypePairs_ShouldHaveMultipleEntries()
        // Assert
        => FileHelper.MimeTypeHelper.ExtensionMimeTypePairs.Count.Should().BeGreaterThan(100);
}
