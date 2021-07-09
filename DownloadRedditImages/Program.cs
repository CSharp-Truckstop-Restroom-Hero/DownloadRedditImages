using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using DownloadRedditImages.Reddit;
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

        private static async Task Main(string[] args)
        {
            if (args.Length == 0)
            {
                Info("Pass a space-separated list of usernames as input. Images for each user will be downloaded to the directory specified in appsettings.json. Alternately, pass \"All\" as an argument to update all previously downloaded authors in the download directory.");
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

            if (args.Length == 1 && args[0].Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                args = Appsettings.DownloadDirectory.GetDirectories()
                    .Where(d => File.Exists(Path.Combine(d.FullName, "checkpoint.json")))
                    .Select(d => d.Name)
                    .ToArray();
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
                if (parsedSubmissionResponse is null)
                {
                    Info("All submissions have been downloaded.");
                    break;
                }

                createdAfter = parsedSubmissionResponse.NewestCreated;
                var parsedImages = parsedSubmissionResponse.ParsedImages;
                Info($"Detecting duplicates in {parsedImages.Count} images...");
                foreach (var (sourceUri, previewUri) in parsedImages)
                {
                    using var previewResponse = await HttpClient.GetAsync(previewUri ?? sourceUri);
                    if (!previewResponse.IsSuccessStatusCode)
                    {
                        Error($"Skipping download of {sourceUri} because the preview {previewUri ?? sourceUri} returned status code {previewResponse.StatusCode}.");
                        continue;
                    }

                    var previewResponseBytes = await previewResponse.Content.ReadAsByteArrayAsync();

                    var castagnoliHash = Crc32CAlgorithm.Compute(previewResponseBytes);
                    if (!castagnoliHashes.Add(castagnoliHash))
                    {
                        Info($"Duplicate image ignored because it had the same Castagnoli hash ({castagnoliHash}) as a previously-downloaded image: {sourceUri}");
                        duplicateCount++;
                        continue;
                    }

                    var perceptualHash = PerceptualHash(previewResponseBytes);
                    if (perceptualHashes.Contains(perceptualHash))
                    {
                        Info($"Duplicate image ignored because it had the same perceptual hash ({perceptualHash}) as a previously-downloaded image: {sourceUri}");
                        duplicateCount++;
                        continue;
                    }

                    if (perceptualDuplicateHashes.Contains(perceptualHash))
                    {
                        Info($"Duplicate image ignored because it had the same perceptual hash ({perceptualHash}) as a previous near-duplicate that was ignored: {sourceUri}");
                        duplicateCount++;
                        continue;
                    }

                    if (IsPerceptualDuplicate(perceptualHash, perceptualHashes, out var hammingDistance, out var perceptualDuplicateOf))
                    {
                        perceptualDuplicateHashes.Add(perceptualHash);
                        Info($"Near-duplicate image ignored because it was within Hamming distance {hammingDistance} of a previously-downloaded image with perceptual hash {perceptualDuplicateOf}: {sourceUri}");
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

                    byte[]? sourceResponseBytes;
                    string? extension;
                    if (previewUri is not null)
                    {
                        using var sourceResponse = await HttpClient.GetAsync(sourceUri);
                        if (!sourceResponse.IsSuccessStatusCode)
                        {
                            Error($"Failed to download {sourceUri}, status code {sourceResponse.StatusCode}");
                            castagnoliHashes.Remove(castagnoliHash);
                            continue;
                        }

                        sourceResponseBytes = await sourceResponse.Content.ReadAsByteArrayAsync();
                        extension = GetFileExtension(sourceResponse);
                    }
                    else
                    {
                        sourceResponseBytes = previewResponseBytes;
                        extension = GetFileExtension(previewResponse);
                    }

                    var filePath = Path.Combine(authorDirectory.FullName, filenameWithoutExtension + extension);
                    await File.WriteAllBytesAsync(filePath, sourceResponseBytes);
                    perceptualHashes.Add(perceptualHash);
                    Info($"Saved {sourceUri} to {filePath}");
                }
                WriteCheckpoint(author, new Checkpoint(createdAfter, castagnoliHashes, perceptualHashes, perceptualDuplicateHashes, duplicateCount));
            }
            Info($"Finished downloading images for user \"{author}\". This user has {duplicateCount} duplicate images.");
        }

        private static Appsettings LoadAppsettings()
        {
            var json = File.ReadAllText(Path.Combine(
                Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!,
                "appsettings.json"));
            var (downloadDirectory, maxHammingDistance) = JsonSerializer.Deserialize<RawAppsettings>(json)!;
            return new Appsettings(new DirectoryInfo(downloadDirectory), maxHammingDistance);
        }

        private static string GetFileExtension(HttpResponseMessage httpResponseMessage) =>
            httpResponseMessage.Content.Headers.TryGetValues("Content-Type", out var values)
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

        private static async Task<ParsedSubmissionResponse?> Get(string author, int createdAfter)
        {
            Info($"Getting submissions after {DateTimeOffset.FromUnixTimeSeconds(createdAfter)}.");
            var response = await HttpClient.GetStringAsync(GetUriFor(author, createdAfter));
            var submissionResponse = JsonSerializer.Deserialize<SubmissionResponse>(response);
            if (submissionResponse?.Data is null || submissionResponse.Data.Count == 0)
            {
                return null;
            }

            var newestCreated = submissionResponse.Data.Max(s => s.Created!.Value);

            List<ParsedImage> parsedImages = new();
            foreach (var submission in submissionResponse.Data)
            {
                var count = parsedImages.Count;

                parsedImages.AddRange(Parse(submission.MediaVariant1?.Images, submission.Uri));
                parsedImages.AddRange(Parse(submission.MediaVariant2?.Values, submission.Uri));

                if (parsedImages.Count == count)
                {
                    Info($"Found no images in post {submission.Uri}");
                }
            }

            return new ParsedSubmissionResponse(parsedImages, newestCreated);
        }

        private static IEnumerable<ParsedImage> Parse<TImage>(IEnumerable<IImageMetadata<TImage>>? imageMetadatas, Uri submissionUri)
        where TImage : IImage
        {
            if (imageMetadatas is null)
            {
                yield break;
            }

            foreach (var imageMetadata in imageMetadatas)
            {
                var parsedImage = Parse(imageMetadata, submissionUri);
                if (parsedImage is not null)
                {
                    yield return parsedImage;
                }
            }
        }

        private static ParsedImage? Parse<TImage>(IImageMetadata<TImage> imageMetadata, Uri submissionUri)
        where TImage : IImage
        {
            if (imageMetadata.Source?.Uri is null)
            {
                Warn($"Rejecting an invalid media from post {submissionUri} because it has no source image: {JsonSerializer.Serialize(imageMetadata)}");
                return null;
            }

            // Note: Reddit sometimes returns image URLs with HTML encoded ampersands, like https://preview.redd.it/yp20vnw3fdw61.jpg?width=108&amp;crop=smart&amp;auto=webp&amp;s=31797b476190709566e62deb9a26ddc5e2ee3f58, so this fixes those URLs.
            var sourceUri = new Uri(WebUtility.HtmlDecode(imageMetadata.Source.Uri.ToString()));

            if (imageMetadata is MediaMetadata mediaMetadata && mediaMetadata.MediaType != "Image")
            {
                Warn($"Rejecting {sourceUri} from post {submissionUri} because its media type was {mediaMetadata.MediaType} instead of \"Image\".");
                return null;
            }

            var smallestPreview = imageMetadata.Previews
                ?.Where(p => p.Height * p.Width > 0)
                .OrderBy(p => p.Height * p.Width)
                .FirstOrDefault()
                ?.Uri;
            if (smallestPreview is null)
            {
                Warn($"An image in post {submissionUri} does not have a preview image. Falling back to using the source image {sourceUri} instead. This may reduce performance. If you see this warning constantly, file a bug.");
                return new ParsedImage(sourceUri, null);
            }

            var previewUri = new Uri(WebUtility.HtmlDecode(smallestPreview.ToString()));
            return new ParsedImage(sourceUri, previewUri);
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
                var json = File.ReadAllText(checkpointFile.FullName);
                var checkpoint = JsonSerializer.Deserialize<Checkpoint>(json) ?? throw new Exception($"Failed to deserialize {json} into Checkpoint.");
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

        private static void Warn(string warn)
        {
            Console.Write('[');
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write("WARN");
            Console.ResetColor();
            Console.WriteLine($"]: {warn}");
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
        Uri? PreviewUri);
}