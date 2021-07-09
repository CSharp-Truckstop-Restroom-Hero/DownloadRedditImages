using System;
using System.Text.Json.Serialization;

namespace DownloadRedditImages.Reddit
{
    internal record Image(
        [property: JsonPropertyName("height")] uint? Height,
        [property: JsonPropertyName("width")] uint? Width,
        [property: JsonPropertyName("url")] Uri? Uri) : IImage;
}