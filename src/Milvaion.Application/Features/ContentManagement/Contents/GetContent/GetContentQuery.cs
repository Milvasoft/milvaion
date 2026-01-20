using Milvaion.Application.Dtos.ContentManagementDtos.ContentDtos;
using Milvasoft.Components.CQRS.Query;

namespace Milvaion.Application.Features.ContentManagement.Contents.GetContent;

/// <summary>
/// Data transfer object for content.
/// </summary>
public record GetContentQuery : IQuery<List<ContentDto>>
{
    /// <summary>
    /// Query for content list.
    /// </summary>
    public string Query { get; set; }

    /// <summary>
    /// Namespace of content.
    /// </summary>
    public string NamespaceSlug { get; set; }

    /// <summary>
    /// Query type for content list.
    /// </summary>
    public ContentQueryType QueryType { get; set; }
}

/// <summary>
/// Query type for content list.
/// </summary>
public enum ContentQueryType
{
    /// <summary>
    /// Query by key.
    /// </summary>
    Key,

    /// <summary>
    /// Query by resource group.
    /// </summary>
    ResourceGroup,

    /// <summary>
    /// Query by namespace.
    /// </summary>
    Namespace,
}