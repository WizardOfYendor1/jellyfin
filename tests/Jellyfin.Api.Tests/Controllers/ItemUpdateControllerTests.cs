using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using AutoFixture.Xunit2;
using Jellyfin.Api.Constants;
using Jellyfin.Api.Controllers;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Jellyfin.Api.Tests.Controllers;

public class ItemUpdateControllerTests
{
    private readonly ItemUpdateController _subject;
    private readonly Mock<ILibraryManager> _mockLibraryManager;
    private readonly Guid _userId = Guid.NewGuid();

    public ItemUpdateControllerTests()
    {
        _mockLibraryManager = new Mock<ILibraryManager>();

        // BaseItem.LibraryManager is a static property used by UpdateToRepositoryAsync.
        BaseItem.LibraryManager = _mockLibraryManager.Object;

        _mockLibraryManager
            .Setup(m => m.UpdateItemAsync(It.IsAny<BaseItem>(), It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _subject = new ItemUpdateController(
            Mock.Of<IFileSystem>(),
            _mockLibraryManager.Object,
            Mock.Of<IProviderManager>(),
            Mock.Of<ILocalizationManager>(),
            Mock.Of<IServerConfigurationManager>());

        var claims = new[] { new Claim(InternalClaimTypes.UserId, _userId.ToString()) };
        _subject.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims))
            }
        };
    }

    private Episode CreateEpisode(params string[] tags)
    {
        return new Episode
        {
            Id = Guid.NewGuid(),
            Tags = tags
        };
    }

    private void SetupItem(BaseItem item)
    {
        _mockLibraryManager
            .Setup(m => m.GetItemById<BaseItem>(item.Id, _userId))
            .Returns(item);
    }

    [Theory]
    [AutoData]
    public async Task UpdateItemTags_WhenItemNotFound_ReturnsNotFound(Guid itemId)
    {
        BaseItem? nullItem = null;
        _mockLibraryManager
            .Setup(m => m.GetItemById<BaseItem>(itemId, _userId))
            .Returns(nullItem);

        Assert.IsType<NotFoundResult>(
            await _subject.UpdateItemTags(itemId, new UpdateItemTagsRequest { Add = ["tag1"] }));
    }

    [Fact]
    public async Task UpdateItemTags_WhenAddingTags_AppendsTags()
    {
        var episode = CreateEpisode("existing");
        SetupItem(episode);

        Assert.IsType<NoContentResult>(
            await _subject.UpdateItemTags(episode.Id, new UpdateItemTagsRequest { Add = ["rating-7", "favorite"] }));

        Assert.Equal(3, episode.Tags.Length);
        Assert.Contains("existing", episode.Tags);
        Assert.Contains("rating-7", episode.Tags);
        Assert.Contains("favorite", episode.Tags);
    }

    [Fact]
    public async Task UpdateItemTags_WhenRemovingTags_RemovesOnlySpecified()
    {
        var episode = CreateEpisode("keep-me", "rating-6", "favorite");
        SetupItem(episode);

        Assert.IsType<NoContentResult>(
            await _subject.UpdateItemTags(episode.Id, new UpdateItemTagsRequest { Remove = ["rating-6", "favorite"] }));

        Assert.Single(episode.Tags);
        Assert.Contains("keep-me", episode.Tags);
    }

    [Fact]
    public async Task UpdateItemTags_WhenAddingAndRemoving_AppliesBoth()
    {
        var episode = CreateEpisode("keep-me", "rating-6");
        SetupItem(episode);

        Assert.IsType<NoContentResult>(
            await _subject.UpdateItemTags(
                episode.Id,
                new UpdateItemTagsRequest { Add = ["rating-7"], Remove = ["rating-6"] }));

        Assert.Equal(2, episode.Tags.Length);
        Assert.Contains("keep-me", episode.Tags);
        Assert.Contains("rating-7", episode.Tags);
        Assert.DoesNotContain("rating-6", episode.Tags);
    }

    [Fact]
    public async Task UpdateItemTags_WhenRequestEmpty_LeavesTagsUnchanged()
    {
        var episode = CreateEpisode("tag1", "tag2");
        SetupItem(episode);

        Assert.IsType<NoContentResult>(
            await _subject.UpdateItemTags(episode.Id, new UpdateItemTagsRequest()));

        Assert.Equal(2, episode.Tags.Length);
        Assert.Contains("tag1", episode.Tags);
        Assert.Contains("tag2", episode.Tags);
    }

    [Fact]
    public async Task UpdateItemTags_WhenAddingDuplicates_DoesNotCreateDuplicates()
    {
        var episode = CreateEpisode("rating-7");
        SetupItem(episode);

        Assert.IsType<NoContentResult>(
            await _subject.UpdateItemTags(episode.Id, new UpdateItemTagsRequest { Add = ["rating-7", "RATING-7"] }));

        Assert.Single(episode.Tags);
    }

    [Fact]
    public async Task UpdateItemTags_WhenRemovingDifferentCase_RemovesCaseInsensitive()
    {
        var episode = CreateEpisode("Favorite");
        SetupItem(episode);

        Assert.IsType<NoContentResult>(
            await _subject.UpdateItemTags(episode.Id, new UpdateItemTagsRequest { Remove = ["favorite"] }));

        Assert.Empty(episode.Tags);
    }

    [Fact]
    public async Task UpdateItemTags_WhenAddingWhitespace_IgnoresWhitespaceEntries()
    {
        var episode = CreateEpisode("existing");
        SetupItem(episode);

        Assert.IsType<NoContentResult>(
            await _subject.UpdateItemTags(episode.Id, new UpdateItemTagsRequest { Add = [string.Empty, "  ", "valid"] }));

        Assert.Equal(2, episode.Tags.Length);
        Assert.Contains("existing", episode.Tags);
        Assert.Contains("valid", episode.Tags);
    }
}
