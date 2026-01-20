using FluentAssertions;
using Milvaion.Application.Dtos.UIDtos;
using Milvaion.Application.Dtos.UIDtos.MenuItemDtos;
using Milvaion.Application.Dtos.UIDtos.PageDtos;

namespace Milvaion.UnitTests.ComponentTests.DtoTests;

[Trait("DTO Unit Tests", "DTO models property and method unit tests.")]
public class UIDtoTests
{
    [Fact]
    public void MenuItemDto_PropertyGetterSetter_ShouldWorkCorrectly()
    {
        // Arrange
        var id = 1;
        var name = "TestName";
        var url = "http://testurl.com";
        var pageName = "TestPage";
        var parentId = 2;
        var group = new MenuGroupDto { Id = 3, Name = "TestGroup" };
        var childrens = new List<MenuItemDto>
        {
            new() { Id = 4, Name = "Child1" },
            new() { Id = 5, Name = "Child2" }
        };

        // Act
        var menuItemDto = new MenuItemDto
        {
            Id = id,
            Name = name,
            Url = url,
            PageName = pageName,
            ParentId = parentId,
            Group = group,
            Childrens = childrens
        };

        // Assert
        menuItemDto.Id.Should().Be(id);
        menuItemDto.Name.Should().Be(name);
        menuItemDto.Url.Should().Be(url);
        menuItemDto.PageName.Should().Be(pageName);
        menuItemDto.ParentId.Should().Be(parentId);
        menuItemDto.Group.Should().Be(group);
        menuItemDto.Childrens.Should().BeEquivalentTo(childrens);
    }

    [Fact]
    public void PageActionDto_PropertyGetterSetter_ShouldWorkCorrectly()
    {
        // Arrange
        var id = 1;
        var title = "TestTitle";
        var actionName = "TestActionName";
        var url = "http://testurl.com";
        var isAction = true;

        // Act
        var pageActionDto = new PageActionDto
        {
            Id = id,
            Title = title,
            ActionName = actionName,
            Url = url,
            IsAction = isAction
        };

        // Assert
        pageActionDto.Id.Should().Be(id);
        pageActionDto.Title.Should().Be(title);
        pageActionDto.ActionName.Should().Be(actionName);
        pageActionDto.Url.Should().Be(url);
        pageActionDto.IsAction.Should().Be(isAction);
    }

    [Fact]
    public void PageDto_PropertyGetterSetter_ShouldWorkCorrectly()
    {
        // Arrange
        var id = 1;
        var name = "TestPage";
        var localizedName = "TestPageLocalized";
        var hasCreate = true;
        var hasDetail = true;
        var hasEdit = true;
        var hasDelete = true;
        var userCanCreate = true;
        var userCanDetail = true;
        var userCanEdit = true;
        var userCanDelete = true;
        var additionalActions = new List<PageActionDto>
        {
            new() { Title = "Action1", ActionName = "Action1Name", Url = "Action1Url", IsAction = true }
        };

        // Act
        var pageDto = new PageDto
        {
            Id = id,
            Name = name,
            LocalizedName = localizedName,
            HasCreate = hasCreate,
            HasDetail = hasDetail,
            HasEdit = hasEdit,
            HasDelete = hasDelete,
            UserCanCreate = userCanCreate,
            UserCanDetail = userCanDetail,
            UserCanEdit = userCanEdit,
            UserCanDelete = userCanDelete,
            AdditionalActions = additionalActions
        };

        // Assert
        pageDto.Id.Should().Be(id);
        pageDto.Name.Should().Be(name);
        pageDto.LocalizedName.Should().Be(localizedName);
        pageDto.HasCreate.Should().Be(hasCreate);
        pageDto.HasDetail.Should().Be(hasDetail);
        pageDto.HasEdit.Should().Be(hasEdit);
        pageDto.HasDelete.Should().Be(hasDelete);
        pageDto.UserCanCreate.Should().Be(userCanCreate);
        pageDto.UserCanDetail.Should().Be(userCanDetail);
        pageDto.UserCanEdit.Should().Be(userCanEdit);
        pageDto.UserCanDelete.Should().Be(userCanDelete);
        pageDto.AdditionalActions.Should().BeEquivalentTo(additionalActions);
    }

    [Fact]
    public void LocalizedContentDto_PropertyGetterSetter_ShouldWorkCorrectly()
    {
        // Arrange
        var key = "TestKey";
        var value = "TestValue";

        // Act
        var localizedContentDto = new LocalizedContentDto
        {
            Key = key,
            Value = value
        };

        // Assert
        localizedContentDto.Key.Should().Be(key);
        localizedContentDto.Value.Should().Be(value);
    }
}
