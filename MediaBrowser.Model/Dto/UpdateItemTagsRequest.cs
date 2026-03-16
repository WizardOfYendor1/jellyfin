namespace MediaBrowser.Model.Dto;

/// <summary>
/// Request body for adding and removing tags on an item
/// without replacing the full item payload.
/// </summary>
public class UpdateItemTagsRequest
{
    /// <summary>
    /// Gets or sets the tags to add.
    /// </summary>
    public string[] Add { get; set; } = [];

    /// <summary>
    /// Gets or sets the tags to remove.
    /// </summary>
    public string[] Remove { get; set; } = [];
}
