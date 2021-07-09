using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DownloadRedditImages.Reddit
{
    internal record MediaMetadata(
        [property: JsonPropertyName("e")] string? MediaType,
        [property: JsonPropertyName("p")] IReadOnlyList<Media>? Previews,
        [property: JsonPropertyName("s")] Media? Source) : IImageMetadata<Media>;
}