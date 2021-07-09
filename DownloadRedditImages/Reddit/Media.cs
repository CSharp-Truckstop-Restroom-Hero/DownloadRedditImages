using System;
using System.Text.Json.Serialization;

namespace DownloadRedditImages.Reddit
{
    internal record Media(
        [property: JsonPropertyName("u")] Uri? Uri,
        [property: JsonPropertyName("x")] uint? Width,
        [property: JsonPropertyName("y")] uint? Height) : IImage;
}