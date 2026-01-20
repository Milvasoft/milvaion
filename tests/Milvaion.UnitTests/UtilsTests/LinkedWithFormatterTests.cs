using FluentAssertions;
using Milvaion.Application.Utils.Constants;
using Milvaion.Application.Utils.LinkedWithFormatters;
using Milvaion.Domain;
using Milvasoft.Core.Abstractions.Localization;
using Milvasoft.Core.MultiLanguage.Manager;
using Milvasoft.Types.Classes;
using Moq;

namespace Milvaion.UnitTests.UtilsTests;

public enum TestEnumFixture
{
    Value1,
    Value2
}

[Trait("Utils Unit Tests", "LinkedWithFormatter unit tests.")]
public class LinkedWithFormatterTests
{
    [Fact]
    public void Format_ByteArrayIsProvided_ShouldReturnBase64String()
    {
        // Arrange
        var formatter = new ByteArrayToBase64Formatter();
        var byteArray = new byte[] { 1, 2, 3, 4, 5 };

        // Act
        var result = formatter.Format(byteArray);

        // Assert
        result.Should().Be("AQIDBAU=");
    }

    [Fact]
    public void Format_NullIsProvided_ShouldReturnEmptyString()
    {
        // Arrange
        var formatter = new ByteArrayToBase64Formatter();

        // Act
        var result = formatter.Format(null);

        // Assert
        result.Should().Be(string.Empty);
    }

    [Fact]
    public void Format_EnumValueIsProvided_ShouldReturnNotLocalizedStringBecauseOfLocalizationManagerIsNull()
    {
        // Arrange
        var mockLocalizer = new Mock<IMilvaLocalizer>();
        mockLocalizer.Setup(l => l["TestEnum.Value1"]).Returns(new LocalizedValue("LocalizedValue1", "Localized Value1"));
        var formatter = new EnumFormatter<TestEnumFixture>(mockLocalizer.Object);

        // Act
        var result = formatter.Format(TestEnumFixture.Value1);

        // Assert
        result.Should().Be("Value1");
    }

    [Fact]
    public void Format_NonEnumValueIsProvided_ShouldReturnOriginalValue()
    {
        // Arrange
        var mockLocalizer = new Mock<IMilvaLocalizer>();
        var formatter = new EnumFormatter<TestEnumFixture>(mockLocalizer.Object);
        var nonEnumValue = "NonEnumValue";

        // Act
        var result = formatter.Format(nonEnumValue);

        // Assert
        result.Should().Be(nonEnumValue);
    }

    [Fact]
    public void Format_NullValueIsProvided_ShouldReturnNull()
    {
        // Arrange
        var mockLocalizer = new Mock<IMilvaLocalizer>();
        var formatter = new EnumFormatter<TestEnumFixture>(mockLocalizer.Object);

        // Act
        var result = formatter.Format(null);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void FormatterName_ShouldReturnExistsNot()
    {
        // Act
        var formatterName = ExistsNotFormatter.FormatterName;

        // Assert
        formatterName.Should().Be("ExistsNot");
    }

    [Fact]
    public void Format_ValueIsTrue_ShouldReturnExists()
    {
        // Arrange
        var mockLocalizer = new Mock<IMilvaLocalizer>();
        mockLocalizer.Setup(l => l["Exists"]).Returns(new LocalizedValue("Exists", "Exists"));
        var formatter = new ExistsNotFormatter(mockLocalizer.Object);

        // Act
        var result = formatter.Format(true);

        // Assert
        result.Should().Be("Exists");
    }

    [Fact]
    public void Format_ValueIsFalse_ShouldReturnNotExists()
    {
        // Arrange
        var mockLocalizer = new Mock<IMilvaLocalizer>();
        mockLocalizer.Setup(l => l["NotExists"]).Returns(new LocalizedValue("NotExists", "NotExists"));
        var formatter = new ExistsNotFormatter(mockLocalizer.Object);

        // Act
        var result = formatter.Format(false);

        // Assert
        result.Should().Be("NotExists");
    }

    [Fact]
    public void Format_ValueIsNotBoolean_ShouldReturnValueAsString()
    {
        // Arrange
        var mockLocalizer = new Mock<IMilvaLocalizer>();
        var formatter = new ExistsNotFormatter(mockLocalizer.Object);
        var value = 123;

        // Act
        var result = formatter.Format(value);

        // Assert
        result.Should().Be("123");
    }

    [Fact]
    public void Format_LanguageIdIsValid_ShouldReturnLanguageName()
    {
        // Arrange
        var formatter = new LanguageIdNameFormatter();
        var languageId = 1;
        var expectedLanguageName = "English";
        MultiLanguageManager.UpdateLanguagesList(
        [
            new Language { Id = languageId, Name = expectedLanguageName }
        ]);

        // Act
        var result = formatter.Format(languageId);

        // Assert
        result.Should().Be(expectedLanguageName);
    }

    [Fact]
    public void Format_LanguageIdIsInvalid_ShouldReturnQuestionMark()
    {
        // Arrange
        var formatter = new LanguageIdNameFormatter();
        var languageId = 99;
        MultiLanguageManager.UpdateLanguagesList(
        [
            new Language { Id = 1, Name = "English" }
        ]);

        // Act
        var result = formatter.Format(languageId);

        // Assert
        result.Should().Be(MessageConstant.QuestionMark);
    }

    [Fact]
    public void Format_ValueIsNotInt_ShouldReturnQuestionMark()
    {
        // Arrange
        var formatter = new LanguageIdNameFormatter();
        var invalidValue = "invalid";

        // Act
        var result = formatter.Format(invalidValue);

        // Assert
        result.Should().Be(MessageConstant.QuestionMark);
    }

    [Fact]
    public void Format_ValueIsString_ShouldReturnLocalizedString()
    {
        // Arrange
        var mockLocalizer = new Mock<IMilvaLocalizer>();
        mockLocalizer.Setup(l => l["UI.TestPage"]).Returns(new LocalizedValue("Test Page Localized", "Test Page Localized"));
        var formatter = new PageNameTranslateFormatter(mockLocalizer.Object);
        var value = "TestPage";

        // Act
        var result = formatter.Format(value);

        // Assert
        result.Should().Be("Test Page Localized");
    }

    [Fact]
    public void Format_ValueIsNotString_ShouldReturnOriginalValue()
    {
        // Arrange
        var mockLocalizer = new Mock<IMilvaLocalizer>();
        var formatter = new PageNameTranslateFormatter(mockLocalizer.Object);
        var value = 123;

        // Act
        var result = formatter.Format(value);

        // Assert
        result.Should().Be("123");
    }

    [Fact]
    public void Format_ValueIsTrue_ShouldReturnYes()
    {
        // Arrange
        var mockLocalizer = new Mock<IMilvaLocalizer>();
        mockLocalizer.Setup(l => l["Yes"]).Returns(new LocalizedValue("Yes", "Yes"));
        var formatter = new YesNoFormatter(mockLocalizer.Object);

        // Act
        var result = formatter.Format(true);

        // Assert
        result.Should().Be("Yes");
    }

    [Fact]
    public void Format_ValueIsFalse_ShouldReturnNo()
    {
        // Arrange
        var mockLocalizer = new Mock<IMilvaLocalizer>();
        mockLocalizer.Setup(l => l["No"]).Returns(new LocalizedValue("No", "No"));
        var formatter = new YesNoFormatter(mockLocalizer.Object);

        // Act
        var result = formatter.Format(false);

        // Assert
        result.Should().Be("No");
    }

    [Fact]
    public void Format_ValueIsNotBoolean_ShouldReturnOriginalValue()
    {
        // Arrange
        var mockLocalizer = new Mock<IMilvaLocalizer>();
        var formatter = new YesNoFormatter(mockLocalizer.Object);
        var value = "NotABoolean";

        // Act
        var result = formatter.Format(value);

        // Assert
        result.Should().Be(value);
    }
}