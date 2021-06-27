using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Force.Crc32;
using Shipwreck.Phash;
using Shipwreck.Phash.Bitmaps;

namespace DownloadRedditImages
{
    internal static class Program
    {
        private static readonly HttpClient HttpClient = new();
        private static readonly Appsettings Appsettings = LoadAppsettings();

        private static Appsettings LoadAppsettings()
        {
            var (outputDirectory, maxHammingDistance) = JsonSerializer.Deserialize<RawAppsettings>(File.ReadAllText(Path.Combine(
                Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
                "appsettings.json")));
            return new Appsettings(new DirectoryInfo(outputDirectory), maxHammingDistance);
        }

        private static async Task Main(string[] args)
        {
            if (args.Length == 0)
            {
                Info("Pass a space-separated list of usernames as input. Images for each user will be downloaded to the directory specified in appsettings.json.");
                return;
            }

            if (!Appsettings.OutputDirectory.Exists)
            {
                Appsettings.OutputDirectory.Create();
            }

            foreach (var author in args)
            {
                await Download(author);
            }
        }

        private static async Task Download(string author)
        {
            Info($"Downloading images for {author}.");
            var (createdAfter, castagnoliHashes, perceptualHashes, perceptualDuplicateHashes) = LoadCheckpoint(author);
            DirectoryInfo authorDirectory = new(Path.Combine(Appsettings.OutputDirectory.FullName, author));
            while (true)
            {
                var parsedSubmissionResponse = await Get(author, createdAfter);
                if (parsedSubmissionResponse == null)
                {
                    Info("All submissions have been downloaded.");
                    break;
                }

                createdAfter = parsedSubmissionResponse.NewestCreated;
                var parsedImages = parsedSubmissionResponse.ParsedImages;
                Info($"Detecting duplicates in {parsedImages.Count} images...");
                foreach (var parsedImage in parsedImages)
                {
                    var decodedPreviewUri = WebUtility.HtmlDecode(parsedImage.PreviewUri.ToString());
                    var decodedSourceUri = WebUtility.HtmlDecode(parsedImage.SourceUri.ToString());
                    using var previewResponse = await HttpClient.GetAsync(decodedPreviewUri);
                    if (!previewResponse.IsSuccessStatusCode)
                    {
                        Error($"Skipping download of {decodedSourceUri} because the preview {decodedPreviewUri} returned status code {previewResponse.StatusCode}.");
                        continue;
                    }

                    var previewResponseBytes = await previewResponse.Content.ReadAsByteArrayAsync();

                    var castagnoliHash = Crc32CAlgorithm.Compute(previewResponseBytes);
                    if (!castagnoliHashes.Add(castagnoliHash))
                    {
                        Info($"Duplicate image ignored because it had the same Castagnoli hash ({castagnoliHash}) as a previously-downloaded image: {decodedSourceUri}");
                        continue;
                    }

                    var perceptualHash = PerceptualHash(previewResponseBytes);
                    if (perceptualHashes.Contains(perceptualHash))
                    {
                        Info($"Duplicate image ignored because it had the same perceptual hash ({perceptualHash}) as a previously-downloaded image: {decodedSourceUri}");
                        continue;
                    }

                    if (perceptualDuplicateHashes.Contains(perceptualHash))
                    {
                        Info($"Duplicate image ignored because it had the same perceptual hash as a previous near-duplicate that was ignored: {decodedSourceUri}");
                        continue;
                    }

                    if (IsPerceptualDuplicate(perceptualHash, perceptualHashes, out var similarity, out var perceptualDuplicateOf))
                    {
                        perceptualDuplicateHashes.Add(perceptualHash);
                        Info($"Near-duplicate image ignored because it had a {similarity}% perceptual hash similarity to a previously-downloaded image with perceptual hash {perceptualDuplicateOf}: {decodedSourceUri}");
                        continue;
                    }

                    var filePath = Path.Combine(authorDirectory.FullName, $"{perceptualHash} {castagnoliHash}.jpg");
                    if (File.Exists(filePath))
                    {
                        perceptualHashes.Add(perceptualHash);
                        Info($"Skipping download because {filePath} already exists.");
                        continue;
                    }

                    var sourceResponse = await HttpClient.GetAsync(decodedSourceUri);
                    if (!sourceResponse.IsSuccessStatusCode)
                    {
                        Error($"Failed to download {decodedPreviewUri}, status code {sourceResponse.StatusCode}");
                        castagnoliHashes.Remove(castagnoliHash);
                        continue;
                    }

                    await File.WriteAllBytesAsync(filePath, await sourceResponse.Content.ReadAsByteArrayAsync());
                    perceptualHashes.Add(perceptualHash);
                    Info($"Saved {decodedSourceUri} to {filePath}");
                }
                WriteCheckpoint(author, new Checkpoint(createdAfter, castagnoliHashes, perceptualHashes, perceptualDuplicateHashes));
            }
        }

        private static bool IsPerceptualDuplicate(
            ulong perceptualHash,
            HashSet<ulong> perceptualHashes,
            out int? hammingDistance,
            out ulong? perceptualDuplicateOf)
        {
            foreach (var existingPerceptualHash in perceptualHashes)
            {
                hammingDistance = ImagePhash.GetHammingDistance(perceptualHash, existingPerceptualHash);
                if (hammingDistance <= Appsettings.MaxHammingDistance)
                {
                    perceptualDuplicateOf = existingPerceptualHash;
                    return true;
                }
            }

            hammingDistance = null;
            perceptualDuplicateOf = null;
            return false;
        }

        private static ulong PerceptualHash(byte[] imageContent)
        {
            using var previewResponseStream = new MemoryStream(imageContent);
            return ImagePhash.ComputeDctHash(((Bitmap) System.Drawing.Image.FromStream(previewResponseStream)).ToLuminanceImage());
        }

        private static async Task<ParsedSubmissionResponse> Get(string author, int createdAfter)
        {
            Info($"Getting submissions after {DateTimeOffset.FromUnixTimeSeconds(createdAfter)}.");
            var response = await HttpClient.GetStringAsync(GetUriFor(author, createdAfter));
            var submissionResponse = JsonSerializer.Deserialize<SubmissionResponse>(response);
            if (submissionResponse!.Data.Count == 0)
            {
                return null;
            }
            List<ParsedImage> parsedImages = new();
            var newestCreated = submissionResponse.Data.Max(s => s.Created);
            foreach (var submission in submissionResponse.Data)
            {
                var media1Parsed = submission?.MediaVariant1?.Images
                   ?.Select(Parse)
                   ?.Where(p => p != null)
                   ?.ToList()
                   ?? new List<ParsedImage>();
                var media2Parsed = submission?.MediaVariant2?.Values
                   ?.Select(Parse)
                   ?.Where(p => p != null)
                   ?.ToList()
                   ?? new List<ParsedImage>();
                var mediaParsed = media1Parsed.Concat(media2Parsed).ToList();
                if (mediaParsed.Any())
                {
                    parsedImages.AddRange(mediaParsed);
                }
                else
                {
                    Info($"Found no images in post {submission?.Uri}");
                }
            }

            return new ParsedSubmissionResponse(parsedImages, newestCreated);
        }

        private static ParsedImage Parse(ImageMetadata imageMetadata)
        {
            if (imageMetadata.Source?.Uri != null)
            {
                var minPreview = imageMetadata?.Previews
                    ?.Where(p => p.Height * p.Width > 0)
                    ?.OrderBy(p => p.Height * p.Width)
                    ?.FirstOrDefault()
                    ?.Uri;
                if (minPreview != null)
                {
                    return new ParsedImage(imageMetadata.Source.Uri, minPreview);
                }
            }
            Error($"Rejecting invalid ImageMetadata: {JsonSerializer.Serialize(imageMetadata)}");
            return null;
        }

        private static ParsedImage Parse(MediaMetadata mediaMetadata)
        {
            if (mediaMetadata.Source?.Uri != null && mediaMetadata.MediaType == "Image")
            {
                var minPreview = mediaMetadata?.Previews
                    ?.Where(p => p.Height * p.Width > 0)
                    ?.OrderBy(p => p.Height * p.Width)
                    ?.FirstOrDefault()
                    ?.Uri;
                if (minPreview != null)
                {
                    return new ParsedImage(mediaMetadata.Source.Uri, minPreview);
                }
            }
            Error($"Rejecting invalid MediaMetadata: {JsonSerializer.Serialize(mediaMetadata)}");
            return null;
        }

        private static Checkpoint LoadCheckpoint(string author)
        {
            DirectoryInfo authorDirectory = new(Path.Combine(Appsettings.OutputDirectory.FullName, author));
            if (!authorDirectory.Exists)
            {
                authorDirectory.Create();
            }

            FileInfo checkpointFile = new(Path.Combine(authorDirectory.FullName, "checkpoint.json"));

            if (checkpointFile.Exists)
            {
                var checkpoint = JsonSerializer.Deserialize<Checkpoint>(File.ReadAllText(checkpointFile.FullName));
                Info($"Resuming from checkpoint timestamp {checkpoint.NewestCreated}");
                return checkpoint;
            }

            return new Checkpoint(0, new HashSet<uint>(), new HashSet<ulong>(), new HashSet<ulong>());
        }

        private static void WriteCheckpoint(
            string author,
            Checkpoint checkpoint)
        {
            File.WriteAllText(
                Path.Combine(Appsettings.OutputDirectory.FullName, author, "checkpoint.json"),
                JsonSerializer.Serialize(checkpoint));
        }

        private static void Error(string error)
        {
            Console.Write('[');
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Write("ERROR");
            Console.ResetColor();
            Console.WriteLine($"]: {error}");
        }

        private static void Info(string info) =>
            Console.WriteLine($"[INFO]: {info}");

        private static Uri GetUriFor(string author, int createdAfter) =>
            new($"https://api.pushshift.io/reddit/search/submission?author={author}&after={createdAfter}&size=500");
    }

    internal record Checkpoint(
        int NewestCreated,
        HashSet<uint> CastagnoliHashes,
        HashSet<ulong> PerceptualHashes,
        HashSet<ulong> PerceptualDuplicateHashes);

    internal record Appsettings(
        DirectoryInfo OutputDirectory,
        ushort MaxHammingDistance);

    internal record RawAppsettings(
        string OutputDirectory,
        ushort MaxHammingDistance);

    internal record ParsedSubmissionResponse(
        IReadOnlyList<ParsedImage> ParsedImages,
        int NewestCreated);

    internal record ParsedImage(
        Uri SourceUri,
        Uri PreviewUri);

    internal record SubmissionResponse(
        [property: JsonPropertyName("data")] IReadOnlyList<Submission> Data);

    internal record Submission(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("created_utc")] int Created,
        [property: JsonPropertyName("media_metadata")] Dictionary<string, MediaMetadata> MediaVariant2,
        [property: JsonPropertyName("preview")] Preview MediaVariant1,
        [property: JsonPropertyName("url")] Uri Uri);

    internal record Preview(
        [property: JsonPropertyName("images")] IReadOnlyList<ImageMetadata> Images);

    internal record ImageMetadata(
        [property: JsonPropertyName("resolutions")] IReadOnlyList<Image> Previews,
        [property: JsonPropertyName("source")] Image Source);

    internal record Image(
        [property: JsonPropertyName("height")] int Height,
        [property: JsonPropertyName("width")] int Width,
        [property: JsonPropertyName("url")] Uri Uri);
    internal record MediaMetadata(
        [property: JsonPropertyName("e")] string MediaType,
        [property: JsonPropertyName("p")] IReadOnlyList<Media> Previews,
        [property: JsonPropertyName("s")] Media Source);

    internal record Media(
        [property: JsonPropertyName("u")] Uri Uri,
        [property: JsonPropertyName("x")] uint Width,
        [property: JsonPropertyName("y")] uint Height);
}