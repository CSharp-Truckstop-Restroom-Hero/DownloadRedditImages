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
using MimeTypes;
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
            var (downloadDirectory, maxHammingDistance) = JsonSerializer.Deserialize<RawAppsettings>(File.ReadAllText(Path.Combine(
                Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
                "appsettings.json")));
            return new Appsettings(new DirectoryInfo(downloadDirectory), maxHammingDistance);
        }

        // TODO: No-preview images
        private static async Task Main(string[] args)
        {
            if (args.Length == 0)
            {
                Info("Pass a space-separated list of usernames as input. Images for each user will be downloaded to the directory specified in appsettings.json.");
                return;
            }

            if (!Appsettings.DownloadDirectory.Exists)
            {
                try
                {
                    Appsettings.DownloadDirectory.Create();
                }
                catch (Exception e)
                {
                    Error($"Failed to create download directory {Appsettings.DownloadDirectory.FullName}.\n{e}");
                    Environment.Exit(1);
                }
            }

            foreach (var author in args)
            {
                try
                {
                    await Download(author);
                }
                catch (Exception e)
                {
                    Error($"An error occurred while downloading images for user \"{author}\". Please try again later. If this error occurs repeatedly, file a bug.\n{e}");
                }
            }
        }

        private static async Task Download(string author)
        {
            Info($"Downloading images for user \"{author}\".");

            var (createdAfter, castagnoliHashes, perceptualHashes, perceptualDuplicateHashes, duplicateCount) = LoadCheckpoint(author);
            DirectoryInfo authorDirectory = new(Path.Combine(Appsettings.DownloadDirectory.FullName, author));
            var filenamesWithoutExtensions = Directory.GetFiles(authorDirectory.FullName)
                .Select(Path.GetFileNameWithoutExtension)
                .ToHashSet();

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
                        duplicateCount++;
                        continue;
                    }

                    var perceptualHash = PerceptualHash(previewResponseBytes);
                    if (perceptualHashes.Contains(perceptualHash))
                    {
                        Info($"Duplicate image ignored because it had the same perceptual hash ({perceptualHash}) as a previously-downloaded image: {decodedSourceUri}");
                        duplicateCount++;
                        continue;
                    }

                    if (perceptualDuplicateHashes.Contains(perceptualHash))
                    {
                        Info($"Duplicate image ignored because it had the same perceptual hash ({perceptualHash}) as a previous near-duplicate that was ignored: {decodedSourceUri}");
                        duplicateCount++;
                        continue;
                    }

                    if (IsPerceptualDuplicate(perceptualHash, perceptualHashes, out var hammingDistance, out var perceptualDuplicateOf))
                    {
                        perceptualDuplicateHashes.Add(perceptualHash);
                        Info($"Near-duplicate image ignored because it was within Hamming distance {hammingDistance} of a previously-downloaded image with perceptual hash {perceptualDuplicateOf}: {decodedSourceUri}");
                        duplicateCount++;
                        continue;
                    }

                    var filenameWithoutExtension = $"{perceptualHash} {castagnoliHash}";
                    if (!filenamesWithoutExtensions.Add(filenameWithoutExtension))
                    {
                        perceptualHashes.Add(perceptualHash);
                        Info($"Skipping download because {filenameWithoutExtension}.* is already present in {authorDirectory.FullName}.");
                        continue;
                    }

                    var sourceResponse = await HttpClient.GetAsync(decodedSourceUri);
                    if (!sourceResponse.IsSuccessStatusCode)
                    {
                        Error($"Failed to download {decodedPreviewUri}, status code {sourceResponse.StatusCode}");
                        castagnoliHashes.Remove(castagnoliHash);
                        continue;
                    }

                    var filePath = Path.Combine(authorDirectory.FullName, filenameWithoutExtension + GetFileExtension(sourceResponse));
                    await File.WriteAllBytesAsync(filePath, await sourceResponse.Content.ReadAsByteArrayAsync());
                    perceptualHashes.Add(perceptualHash);
                    Info($"Saved {decodedSourceUri} to {filePath}");
                }
                WriteCheckpoint(author, new Checkpoint(createdAfter, castagnoliHashes, perceptualHashes, perceptualDuplicateHashes, duplicateCount));
            }
            Info($"Finished downloading images for user \"{author}\". This user has {duplicateCount} duplicate images.");
        }

        private static string GetFileExtension(HttpResponseMessage httpResponseMessage) =>
            httpResponseMessage.Content.Headers.TryGetValues("Content-Type", out var values) && values.Count() == 1
                ? MimeTypeMap.GetExtension(values.Single())
                : ".jpg";

        private static bool IsPerceptualDuplicate(
            ulong perceptualHash,
            HashSet<ulong> perceptualHashes,
            out int? hammingDistance,
            out ulong? perceptualDuplicateOf)
        {
            foreach (var existingPerceptualHash in perceptualHashes)
            {
                var currentHammingDistance = ImagePhash.GetHammingDistance(perceptualHash, existingPerceptualHash);
                if (currentHammingDistance <= Appsettings.MaxHammingDistance)
                {
                    hammingDistance = currentHammingDistance;
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
            using var bitmap = (Bitmap) System.Drawing.Image.FromStream(previewResponseStream);
            return ImagePhash.ComputeDctHash(bitmap.ToLuminanceImage());
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
            DirectoryInfo authorDirectory = new(Path.Combine(Appsettings.DownloadDirectory.FullName, author));
            if (!authorDirectory.Exists)
            {
                authorDirectory.Create();
            }

            FileInfo checkpointFile = new(Path.Combine(authorDirectory.FullName, "checkpoint.json"));

            if (checkpointFile.Exists)
            {
                var checkpoint = JsonSerializer.Deserialize<Checkpoint>(File.ReadAllText(checkpointFile.FullName));
                Info($"Resuming from checkpoint timestamp {DateTimeOffset.FromUnixTimeSeconds(checkpoint.NewestCreated)}");
                return checkpoint;
            }

            return new Checkpoint(0, new HashSet<uint>(), new HashSet<ulong>(), new HashSet<ulong>(), 0);
        }

        private static void WriteCheckpoint(
            string author,
            Checkpoint checkpoint)
        {
            File.WriteAllText(
                Path.Combine(Appsettings.DownloadDirectory.FullName, author, "checkpoint.json"),
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
        HashSet<ulong> PerceptualDuplicateHashes,
        int DuplicateCount);

    internal record Appsettings(
        DirectoryInfo DownloadDirectory,
        ushort MaxHammingDistance);

    internal record RawAppsettings(
        string DownloadDirectory,
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