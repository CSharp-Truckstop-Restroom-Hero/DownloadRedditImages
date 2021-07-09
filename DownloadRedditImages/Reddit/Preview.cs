using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DownloadRedditImages.Reddit
{
    internal record Preview(
        [property: JsonPropertyName("images")] IReadOnlyList<ImageMetadata>? Images);
}