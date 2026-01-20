using FluentAssertions;
using Milvaion.Domain.JsonModels;
using Milvaion.Domain.UI;

namespace Milvaion.UnitTests.ComponentTests;

[Trait("Json Model's Property Getter Setters Unit Tests", "Json models property getter setter unit tests.")]
public class JsonModelTests
{
    [Fact]
    public void MenuGroupTranslation_PropertyGetterSetter_ShouldWorkCorrectly()
    {
        // Arrange
        var name = "TestName";
        var languageId = 1;
        var entityId = 1;
        var entity = new MenuGroup();

        // Act
        var menuGroupTranslation = new MenuGroupTranslation
        {
            Name = name,
            LanguageId = languageId,
            EntityId = entityId,
            Entity = entity
        };

        // Assert
        menuGroupTranslation.Name.Should().Be(name);
        menuGroupTranslation.LanguageId.Should().Be(languageId);
        menuGroupTranslation.EntityId.Should().Be(entityId);
        menuGroupTranslation.Entity.Should().Be(entity);
    }

    [Fact]
    public void MenuItemTranslation_PropertyGetterSetter_ShouldWorkCorrectly()
    {
        // Arrange
        var name = "TestName";
        var languageId = 1;
        var entityId = 1;
        var entity = new MenuItem { Url = "TestUrl", PageName = "TestPageName" };

        // Act
        var menuItemTranslation = new MenuItemTranslation
        {
            Name = name,
            LanguageId = languageId,
            EntityId = entityId,
            Entity = entity
        };

        // Assert
        menuItemTranslation.Name.Should().Be(name);
        menuItemTranslation.LanguageId.Should().Be(languageId);
        menuItemTranslation.EntityId.Should().Be(entityId);
        menuItemTranslation.Entity.Should().Be(entity);
    }

    [Fact]
    public void PageActionTranslation_PropertyGetterSetter_ShouldWorkCorrectly()
    {
        // Arrange
        var title = "TestTitle";
        var languageId = 1;
        var entityId = 1;
        var entity = new PageAction();

        // Act
        var pageActionTranslation = new PageActionTranslation
        {
            Title = title,
            LanguageId = languageId,
            EntityId = entityId,
            Entity = entity
        };

        // Assert
        pageActionTranslation.Title.Should().Be(title);
        pageActionTranslation.LanguageId.Should().Be(languageId);
        pageActionTranslation.EntityId.Should().Be(entityId);
        pageActionTranslation.Entity.Should().Be(entity);
    }

    [Fact]
    public void RequestInfo_PropertyGetterSetter_ShouldWorkCorrectly()
    {
        // Arrange
        var method = "GET";
        var absoluteUri = "https://example.com";
        var queryString = "?param=value";
        var headers = new { Header1 = "value1", Header2 = "value2" };
        var contentLength = 12345L;
        var body = new { Key = "value" };

        // Act
        var requestInfo = new RequestInfo
        {
            Method = method,
            AbsoluteUri = absoluteUri,
            QueryString = queryString,
            Headers = headers,
            ContentLength = contentLength,
            Body = body,
        };

        // Assert
        requestInfo.Method.Should().Be(method);
        requestInfo.AbsoluteUri.Should().Be(absoluteUri);
        requestInfo.QueryString.Should().Be(queryString);
        requestInfo.Headers.Should().Be(headers);
        requestInfo.ContentLength.Should().Be(contentLength);
        requestInfo.Body.Should().Be(body);
    }

    [Fact]
    public void ResponseInfo_PropertyGetterSetter_ShouldWorkCorrectly()
    {
        // Arrange
        var statusCode = 200;
        var headers = new { Header1 = "Value1", Header2 = "Value2" };
        var length = 12345L;
        var body = new { Data = "Sample data" };
        var contentType = "application/json";

        // Act
        var responseInfo = new ResponseInfo
        {
            StatusCode = statusCode,
            Headers = headers,
            Length = length,
            Body = body,
            ContentType = contentType
        };

        // Assert
        responseInfo.StatusCode.Should().Be(statusCode);
        responseInfo.Headers.Should().Be(headers);
        responseInfo.Length.Should().Be(length);
        responseInfo.Body.Should().Be(body);
        responseInfo.ContentType.Should().Be(contentType);
    }
}