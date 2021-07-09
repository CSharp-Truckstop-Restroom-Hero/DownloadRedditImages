using System.Collections.Generic;

namespace DownloadRedditImages.Reddit
{
    internal interface IImageMetadata<out TImage> where TImage : IImage
    {
        IReadOnlyList<TImage>? Previews { get; }
        TImage? Source { get; }
    }
}