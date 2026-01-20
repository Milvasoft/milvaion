using FluentAssertions;
using Microsoft.EntityFrameworkCore.Query;
using Milvaion.Application.Dtos;
using Milvaion.Application.Dtos.ContentManagementDtos.ContentDtos;
using Milvaion.Application.Dtos.ContentManagementDtos.LanguageDtos;
using Milvaion.Application.Dtos.ContentManagementDtos.NamespaceDtos;
using Milvaion.Application.Dtos.ContentManagementDtos.ResourceGroupDtos;
using Milvaion.Application.Utils.Extensions;
using Milvaion.Domain;
using Milvaion.Domain.ContentManagement;
using System.Linq.Expressions;

namespace Milvaion.UnitTests.ComponentTests.DtoTests;

[Trait("DTO Unit Tests", "DTO models property and method unit tests.")]
public class ContentManagementDtoTests
{
    [Fact]
    public void ContentDetailDto_PropertyGetterSetter_ShouldWorkCorrectly()
    {
        // Arrange
        var id = 1;
        var key = "TestKey";
        var value = "TestValue";
        var languageId = 1;
        var languageName = "TestLanguageName";
        var namespaceSlug = "TestNamespaceSlug";
        var resourceGroupSlug = "TestResourceGroupSlug";
        var keyAlias = "TestNamespaceSlug.TestResourceGroupSlug.TestKey";
        var namespaceDto = new NameIntNavigationDto { Id = 1, Name = "TestNamespace" };
        var resourceGroupDto = new NameIntNavigationDto { Id = 1, Name = "TestResourceGroup" };
        var medias = new List<MediaDto> { new() { Id = 1, Type = "image", Value = [1, 2, 3] } };
        var auditInfo = new AuditDto<int>(new Content { CreationDate = DateTime.Now, CreatorUserName = "TestUser" });

        // Act
        var contentDetailDto = new ContentDetailDto
        {
            Id = id,
            Key = key,
            Value = value,
            LanguageId = languageId,
            LanguageName = languageName,
            NamespaceSlug = namespaceSlug,
            ResourceGroupSlug = resourceGroupSlug,
            KeyAlias = keyAlias,
            Namespace = namespaceDto,
            ResourceGroup = resourceGroupDto,
            Medias = medias,
            AuditInfo = auditInfo
        };

        // Assert
        contentDetailDto.Id.Should().Be(id);
        contentDetailDto.Key.Should().Be(key);
        contentDetailDto.Value.Should().Be(value);
        contentDetailDto.LanguageId.Should().Be(languageId);
        contentDetailDto.LanguageName.Should().Be(languageName);
        contentDetailDto.NamespaceSlug.Should().Be(namespaceSlug);
        contentDetailDto.ResourceGroupSlug.Should().Be(resourceGroupSlug);
        contentDetailDto.KeyAlias.Should().Be(keyAlias);
        contentDetailDto.Namespace.Should().BeEquivalentTo(namespaceDto);
        contentDetailDto.ResourceGroup.Should().BeEquivalentTo(resourceGroupDto);
        contentDetailDto.Medias.Should().BeEquivalentTo(medias);
        contentDetailDto.AuditInfo.Should().BeEquivalentTo(auditInfo);
    }
    [Fact]
    public void ContentDetailDto_Projection_ShouldReturnCorrectly()
    {
        // Arrange
        Expression<Func<Content, ContentDetailDto>> expectedExpression = c => new ContentDetailDto
        {
            Id = c.Id,
            Key = c.Key,
            Value = c.Value,
            LanguageId = c.LanguageId,
            NamespaceSlug = c.NamespaceSlug,
            ResourceGroupSlug = c.ResourceGroupSlug,
            KeyAlias = c.KeyAlias,
            Namespace = new NameIntNavigationDto
            {
                Id = c.NamespaceId,
                Name = c.Namespace.Name
            },
            ResourceGroup = new NameIntNavigationDto
            {
                Id = c.ResourceGroupId,
                Name = c.ResourceGroup.Name
            },
            Medias = c.Medias.Select(m => new MediaDto
            {
                Id = m.Id,
                Type = m.Type,
                Value = m.Value
            }).ToList(),
            AuditInfo = new AuditDto<int>(c)
        };

        // Act
        var result = ContentDetailDto.Projection;

        // Assert
        var equality = ExpressionEqualityComparer.Instance.Equals(expectedExpression, result);

        equality.Should().BeTrue();
    }

    [Fact]
    public void ContentDto_PropertyGetterSetter_ShouldWorkCorrectly()
    {
        // Arrange
        var id = 1;
        var key = "TestKey";
        var value = "TestValue";
        var languageId = 1;
        var namespaceSlug = "TestNamespaceSlug";
        var resourceGroupSlug = "TestResourceGroupSlug";
        var keyAlias = "TestNamespaceSlug.TestResourceGroupSlug.TestKey";
        var medias = new List<MediaDto> { new() { Type = "image", Value = [1, 2, 3] } };

        // Act
        var contentDto = new ContentDto
        {
            Id = id,
            Key = key,
            Value = value,
            LanguageId = languageId,
            NamespaceSlug = namespaceSlug,
            ResourceGroupSlug = resourceGroupSlug,
            KeyAlias = keyAlias,
            Medias = medias,
        };

        // Assert
        contentDto.Id.Should().Be(id);
        contentDto.Key.Should().Be(key);
        contentDto.Value.Should().Be(value);
        contentDto.LanguageId.Should().Be(languageId);
        contentDto.NamespaceSlug.Should().Be(namespaceSlug);
        contentDto.ResourceGroupSlug.Should().Be(resourceGroupSlug);
        contentDto.KeyAlias.Should().Be(keyAlias);
        contentDto.Medias.Should().BeEquivalentTo(medias);
    }

    [Fact]
    public void ContentDto_Projection_ShouldReturnCorrectly()
    {
        // Arrange
        Expression<Func<Content, ContentDto>> expectedExpression = c => new ContentDto
        {
            Id = c.Id,
            Key = c.Key,
            Value = c.Value,
            LanguageId = c.LanguageId,
            NamespaceSlug = c.NamespaceSlug,
            ResourceGroupSlug = c.ResourceGroupSlug,
            KeyAlias = c.KeyAlias,
            Medias = c.Medias.Select(m => new MediaDto
            {
                Id = m.Id,
                Type = m.Type,
                Value = m.Value
            }).ToList(),
        };

        // Act
        var result = ContentDto.Projection;

        // Assert
        var equality = ExpressionEqualityComparer.Instance.Equals(expectedExpression, result);

        equality.Should().BeTrue();
    }

    [Fact]
    public void ContentListDto_PropertyGetterSetter_ShouldWorkCorrectly()
    {
        // Arrange
        var id = 1;
        var keyAlias = "namespaceSlug.resourceGroupSlug.key";
        var key = "TestKey";
        var value = "TestValue";
        var languageId = 1;
        var languageName = "English";
        var namespaceSlug = "TestNamespaceSlug";
        var resourceGroupSlug = "TestResourceGroupSlug";
        var namespaceDto = new NameIntNavigationDto { Id = 1, Name = "TestNamespace" };
        var resourceGroupDto = new NameIntNavigationDto { Id = 1, Name = "TestResourceGroup" };

        // Act
        var contentListDto = new ContentListDto
        {
            Id = id,
            KeyAlias = keyAlias,
            Key = key,
            Value = value,
            LanguageId = languageId,
            LanguageName = languageName,
            NamespaceSlug = namespaceSlug,
            ResourceGroupSlug = resourceGroupSlug,
            Namespace = namespaceDto,
            ResourceGroup = resourceGroupDto
        };

        // Assert
        contentListDto.Id.Should().Be(id);
        contentListDto.KeyAlias.Should().Be(keyAlias);
        contentListDto.Key.Should().Be(key);
        contentListDto.Value.Should().Be(value);
        contentListDto.LanguageId.Should().Be(languageId);
        contentListDto.LanguageName.Should().Be(languageName);
        contentListDto.NamespaceSlug.Should().Be(namespaceSlug);
        contentListDto.ResourceGroupSlug.Should().Be(resourceGroupSlug);
        contentListDto.Namespace.Should().BeEquivalentTo(namespaceDto);
        contentListDto.ResourceGroup.Should().BeEquivalentTo(resourceGroupDto);
    }

    [Fact]
    public void ContentListDto_Projection_ShouldReturnCorrectly()
    {
        // Arrange
        Expression<Func<Content, ContentListDto>> expectedExpression = c => new ContentListDto
        {
            Id = c.Id,
            Key = c.Key,
            Value = c.Value,
            LanguageId = c.LanguageId,
            NamespaceSlug = c.NamespaceSlug,
            ResourceGroupSlug = c.ResourceGroupSlug,
            KeyAlias = c.KeyAlias,
            Namespace = new NameIntNavigationDto
            {
                Id = c.NamespaceId,
                Name = c.Namespace.Name
            },
            ResourceGroup = new NameIntNavigationDto
            {
                Id = c.ResourceGroupId,
                Name = c.ResourceGroup.Name
            }
        };

        // Act
        var result = ContentListDto.Projection;

        // Assert
        var equality = ExpressionEqualityComparer.Instance.Equals(expectedExpression, result);

        equality.Should().BeTrue();
    }

    [Fact]
    public void CreateContentDto_PropertyGetterSetter_ShouldWorkCorrectly()
    {
        // Arrange
        var key = "TestKey";
        var value = "TestValue";
        var languageId = 1;
        var resourceGroupId = 1;
        var medias = new List<UpsertMediaDto>
        {
            new() { MediaAsBase64 = "TestBase64", Type = "image" }
        };

        // Act
        var createContentDto = new CreateContentDto
        {
            Key = key,
            Value = value,
            LanguageId = languageId,
            ResourceGroupId = resourceGroupId,
            Medias = medias,
        };

        // Assert
        createContentDto.Key.Should().Be(key);
        createContentDto.Value.Should().Be(value);
        createContentDto.LanguageId.Should().Be(languageId);
        createContentDto.ResourceGroupId.Should().Be(resourceGroupId);
        createContentDto.Medias.Should().BeEquivalentTo(medias);
    }

    [Fact]
    public void GroupedContentListDto_PropertyGetterSetter_ShouldWorkCorrectly()
    {
        // Arrange
        var idList = new List<int> { 1, 2, 3 };
        var keyAlias = "namespaceSlug.resourceGroupSlug.key";
        var key = "key";
        var namespaceSlug = "namespaceSlug";
        var resourceGroupSlug = "resourceGroupSlug";
        var namespaceDto = new NameIntNavigationDto { Id = 1, Name = "NamespaceName" };
        var resourceGroupDto = new NameIntNavigationDto { Id = 2, Name = "ResourceGroupName" };

        // Act
        var dto = new GroupedContentListDto
        {
            IdList = idList,
            KeyAlias = keyAlias,
            Key = key,
            NamespaceSlug = namespaceSlug,
            ResourceGroupSlug = resourceGroupSlug,
            Namespace = namespaceDto,
            ResourceGroup = resourceGroupDto
        };

        // Assert
        dto.IdList.Should().BeEquivalentTo(idList);
        dto.KeyAlias.Should().Be(keyAlias);
        dto.Key.Should().Be(key);
        dto.NamespaceSlug.Should().Be(namespaceSlug);
        dto.ResourceGroupSlug.Should().Be(resourceGroupSlug);
        dto.Namespace.Should().Be(namespaceDto);
        dto.ResourceGroup.Should().Be(resourceGroupDto);
    }

    [Fact]
    public void GroupedContentListDto_Projection_ShouldReturnCorrectly()
    {
        // Arrange
        Expression<Func<Content, GroupedContentListDto>> expectedExpression = c => new GroupedContentListDto
        {
            Key = c.Key,
            NamespaceSlug = c.NamespaceSlug,
            ResourceGroupSlug = c.ResourceGroupSlug,
            KeyAlias = c.KeyAlias,
            Namespace = new NameIntNavigationDto
            {
                Id = c.NamespaceId,
                Name = c.Namespace.Name
            },
            ResourceGroup = new NameIntNavigationDto
            {
                Id = c.ResourceGroupId,
                Name = c.ResourceGroup.Name
            }
        };

        // Act
        var result = GroupedContentListDto.Projection;

        // Assert
        var equality = ExpressionEqualityComparer.Instance.Equals(expectedExpression, result);

        equality.Should().BeTrue();
    }

    [Fact]
    public void MediaDto_PropertyGetterSetter_ShouldWorkCorrectly()
    {
        // Arrange
        var value = new byte[] { 1, 2, 3, 4 };
        var mediaAsBase64 = Convert.ToBase64String(value);
        var type = "image";

        // Act
        var mediaDto = new MediaDto
        {
            Value = value,
            MediaAsBase64 = mediaAsBase64,
            Type = type
        };

        // Assert
        mediaDto.Value.Should().BeEquivalentTo(value);
        mediaDto.MediaAsBase64.Should().Be(mediaAsBase64);
        mediaDto.Type.Should().Be(type);
    }

    [Fact]
    public void UpsertMediaDto_PropertyGetterSetter_ShouldWorkCorrectly()
    {
        // Arrange
        var mediaAsBase64 = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAUA";
        var type = "image";
        var expectedMedia = MilvaionExtensions.DataUriToPlainText(mediaAsBase64);

        // Act
        var upsertMediaDto = new UpsertMediaDto
        {
            MediaAsBase64 = mediaAsBase64,
            Type = type
        };

        // Assert
        upsertMediaDto.MediaAsBase64.Should().Be(mediaAsBase64);
        upsertMediaDto.Type.Should().Be(type);
        upsertMediaDto.Media.Should().BeEquivalentTo(expectedMedia);
    }

    [Fact]
    public void LanguageDto_PropertyGetterSetter_ShouldWorkCorrectly()
    {
        // Arrange
        var id = 1;
        var name = "English";
        var code = "EN";
        var supported = true;
        var supportedDescription = "Yes";
        var isDefault = true;
        var isDefaultDescription = "Yes";

        // Act
        var languageDto = new LanguageDto
        {
            Id = id,
            Name = name,
            Code = code,
            Supported = supported,
            SupportedDescription = supportedDescription,
            IsDefault = isDefault,
            IsDefaultDescription = isDefaultDescription
        };

        // Assert
        languageDto.Id.Should().Be(id);
        languageDto.Name.Should().Be(name);
        languageDto.Code.Should().Be(code);
        languageDto.Supported.Should().Be(supported);
        languageDto.SupportedDescription.Should().Be(supportedDescription);
        languageDto.IsDefault.Should().Be(isDefault);
        languageDto.IsDefaultDescription.Should().Be(isDefaultDescription);
    }

    [Fact]
    public void Language_Projection_ShouldReturnCorrectly()
    {
        // Arrange
        Expression<Func<Language, LanguageDto>> expectedExpression = n => new LanguageDto
        {
            Id = n.Id,
            Name = n.Name,
            Code = n.Code,
            Supported = n.Supported,
            IsDefault = n.IsDefault,
        };

        // Act
        var result = LanguageDto.Projection;

        // Assert
        var equality = ExpressionEqualityComparer.Instance.Equals(expectedExpression, result);

        equality.Should().BeTrue();
    }

    [Fact]
    public void NamespaceDetailDto_PropertyGetterSetter_ShouldWorkCorrectly()
    {
        // Arrange
        var id = 1;
        var slug = "TestSlug";
        var name = "TestName";
        var description = "TestDescription";
        var auditInfo = new AuditDto<int>(new Namespace { Id = id });

        // Act
        var namespaceDetailDto = new NamespaceDetailDto
        {
            Id = id,
            Slug = slug,
            Name = name,
            Description = description,
            AuditInfo = auditInfo
        };

        // Assert
        namespaceDetailDto.Id.Should().Be(id);
        namespaceDetailDto.Slug.Should().Be(slug);
        namespaceDetailDto.Name.Should().Be(name);
        namespaceDetailDto.Description.Should().Be(description);
        namespaceDetailDto.AuditInfo.Should().Be(auditInfo);
    }

    [Fact]
    public void NamespaceListDto_PropertyGetterSetter_ShouldWorkCorrectly()
    {
        // Arrange
        var id = 1;
        var slug = "TestSlug";
        var name = "TestName";
        var description = "TestDescription";

        // Act
        var namespaceListDto = new NamespaceListDto
        {
            Id = id,
            Slug = slug,
            Name = name,
            Description = description
        };

        // Assert
        namespaceListDto.Id.Should().Be(id);
        namespaceListDto.Slug.Should().Be(slug);
        namespaceListDto.Name.Should().Be(name);
        namespaceListDto.Description.Should().Be(description);
    }

    [Fact]
    public void NamespaceListDto_Projection_ShouldReturnCorrectly()
    {
        // Arrange
        Expression<Func<Namespace, NamespaceListDto>> expectedExpression = n => new NamespaceListDto
        {
            Id = n.Id,
            Slug = n.Slug,
            Name = n.Name,
            Description = n.Description
        };

        // Act
        var result = NamespaceListDto.Projection;

        // Assert
        var equality = ExpressionEqualityComparer.Instance.Equals(expectedExpression, result);

        equality.Should().BeTrue();
    }

    [Fact]
    public void ResourceGroupDetailDto_PropertyGetterSetter_ShouldWorkCorrectly()
    {
        // Arrange
        var id = 1;
        var slug = "TestSlug";
        var name = "TestName";
        var description = "TestDescription";
        var namespaceDto = new NameIntNavigationDto { Id = 1, Name = "TestNamespace" };
        var auditInfo = new AuditDto<int>(new ResourceGroup());

        // Act
        var dto = new ResourceGroupDetailDto
        {
            Id = id,
            Slug = slug,
            Name = name,
            Description = description,
            Namespace = namespaceDto,
            AuditInfo = auditInfo
        };

        // Assert
        dto.Id.Should().Be(id);
        dto.Slug.Should().Be(slug);
        dto.Name.Should().Be(name);
        dto.Description.Should().Be(description);
        dto.Namespace.Should().BeEquivalentTo(namespaceDto);
        dto.AuditInfo.Should().BeEquivalentTo(auditInfo);
    }

    [Fact]
    public void ResourceGroupListDto_PropertyGetterSetter_ShouldWorkCorrectly()
    {
        // Arrange
        var id = 1;
        var slug = "TestSlug";
        var name = "TestName";
        var description = "TestDescription";
        var namespaceDto = new NameIntNavigationDto { Id = 1, Name = "TestNamespace" };

        // Act
        var resourceGroupListDto = new ResourceGroupListDto
        {
            Id = id,
            Slug = slug,
            Name = name,
            Description = description,
            Namespace = namespaceDto
        };

        // Assert
        resourceGroupListDto.Id.Should().Be(id);
        resourceGroupListDto.Slug.Should().Be(slug);
        resourceGroupListDto.Name.Should().Be(name);
        resourceGroupListDto.Description.Should().Be(description);
        resourceGroupListDto.Namespace.Should().BeEquivalentTo(namespaceDto);
    }

    [Fact]
    public void ResourceGroupListDto_Projection_ShouldReturnCorrectly()
    {
        // Arrange
        Expression<Func<ResourceGroup, ResourceGroupListDto>> expectedExpression = n => new ResourceGroupListDto
        {
            Id = n.Id,
            Slug = n.Slug,
            Name = n.Name,
            Description = n.Description,
            Namespace = new NameIntNavigationDto
            {
                Id = n.Namespace.Id,
                Name = n.Namespace.Name
            }
        };

        // Act
        var result = ResourceGroupListDto.Projection;

        // Assert
        var equality = ExpressionEqualityComparer.Instance.Equals(expectedExpression, result);

        equality.Should().BeTrue();
    }
}
