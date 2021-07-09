using System;

namespace DownloadRedditImages.Reddit
{
    internal interface IImage
    {
        Uri? Uri { get; }
        uint? Width { get; }
        uint? Height { get; }
    }
}