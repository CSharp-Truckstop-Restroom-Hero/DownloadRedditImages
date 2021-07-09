using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DownloadRedditImages.Reddit
{
    internal record Submission(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("created_utc")] int? Created,
        [property: JsonPropertyName("media_metadata")] Dictionary<string, MediaMetadata>? MediaVariant2,
        [property: JsonPropertyName("preview")] Preview? MediaVariant1,
        [property: JsonPropertyName("url")] Uri Uri);
}