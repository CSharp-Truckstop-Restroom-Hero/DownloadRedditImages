using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DownloadRedditImages.Reddit
{
    internal record ImageMetadata(
        [property: JsonPropertyName("resolutions")] IReadOnlyList<Image>? Previews,
        [property: JsonPropertyName("source")] Image? Source) : IImageMetadata<Image>;
}