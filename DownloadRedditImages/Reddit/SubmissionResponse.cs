using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DownloadRedditImages.Reddit
{
    internal record SubmissionResponse(
        [property: JsonPropertyName("data")] IReadOnlyList<Submission>? Data);
}