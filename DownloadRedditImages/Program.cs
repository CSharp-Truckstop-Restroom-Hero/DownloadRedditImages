using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using DownloadRedditImages.Reddit;
using Force.Crc32;
using MimeTypes;
using Serilog;
using Serilog.Events;
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
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Is(Appsettings.MinimumLogEventLevel)
                .WriteTo.Async(loggerSinkConfiguration => loggerSinkConfiguration.Console())
                .CreateLogger();
            try
            {
                if (args.Length == 0)
                {
                    Log.Information("Pass a space-separated list of usernames as input. Images for each user will be downloaded to the directory specified in appsettings.json. If a user already has images downloaded, download will resume where the previous download ended. Pass \"All\" as an argument to update all previously downloaded authors in the download directory.");
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
                        Log.Fatal($"Failed to create download directory {Appsettings.DownloadDirectory.FullName}.\n{e}");
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
                        Log.Error($"An error occurred while downloading images for user \"{author}\". Please try again later. If this error occurs repeatedly, file a bug.\n{e}");
                    }
                }
            }
            finally
            {
                Log.CloseAndFlush();
            }
        }

        private static async Task Download(string author)
        {
            Log.Information($"Downloading images for user \"{author}\".");
            var imagesCount = 0;
            var stopwatch = Stopwatch.StartNew();

            var (
                createdAfter,
                castagnoliHashes,
                perceptualHashes,
                perceptualDuplicateHashes,
                duplicateCount) = LoadCheckpoint(author);
            var castagnoliHashset = castagnoliHashes.ToConcurrentDictionary();
            var perceptualHashset = perceptualHashes.ToConcurrentDictionary();
            var perceptualDuplicateHashset = perceptualDuplicateHashes.ToConcurrentDictionary();
            var duplicates = new Duplicates(duplicateCount);
            DirectoryInfo authorDirectory = new(Path.Combine(Appsettings.DownloadDirectory.FullName, author));
            var filenamesWithoutExtensions = Directory.GetFiles(authorDirectory.FullName)
                .Select(p => Path.GetFileNameWithoutExtension(p)!)
                .ToConcurrentDictionary();
            var castagnoliHashLock = new KeyedLock<uint>();
            var perceptualHashLock = new KeyedLock<ulong>();

            var parsedSubmissionResponse = await Get(author, createdAfter);
            while (true)
            {
                if (parsedSubmissionResponse is null)
                {
                    Log.Information("All submissions have been downloaded.");
                    break;
                }

                createdAfter = parsedSubmissionResponse.NewestCreated;
                var parsedImages = new ConcurrentQueue<ParsedImage>(parsedSubmissionResponse.ParsedImages);
                imagesCount += parsedImages.Count;
                Log.Information($"Detecting duplicates in {parsedImages.Count} images...");
                var nextSubmissionResponse = Get(author, createdAfter);
                var tasks = new List<Task> { nextSubmissionResponse };
                for (var i = 0; i < Appsettings.MaxParallelism; i++)
                {
                    tasks.Add(DownloadImages(
                        parsedImages,
                        castagnoliHashset,
                        perceptualHashset,
                        perceptualDuplicateHashset,
                        filenamesWithoutExtensions,
                        authorDirectory,
                        duplicates,
                        castagnoliHashLock,
                        perceptualHashLock));
                }

                await Task.WhenAll(tasks);
                WriteCheckpoint(author, new Checkpoint(createdAfter, castagnoliHashset.Keys, perceptualHashset.Keys, perceptualDuplicateHashset.Keys, duplicates.Count));
                parsedSubmissionResponse = nextSubmissionResponse.Result;
                Log.Information($"Average image fetch rate: {imagesCount / stopwatch.Elapsed.TotalSeconds} images/second.");
            }
            Log.Information($"Finished downloading images for user \"{author}\". This user has {duplicates.Count} duplicate images.");
        }

        private static async Task DownloadImages(
            ConcurrentQueue<ParsedImage> parsedImages,
            ConcurrentDictionary<uint, bool> castagnoliHashes,
            ConcurrentDictionary<ulong, bool> perceptualHashes,
            ConcurrentDictionary<ulong, bool> perceptualDuplicateHashes,
            ConcurrentDictionary<string, bool> filenamesWithoutExtensions,
            DirectoryInfo authorDirectory,
            Duplicates duplicates,
            KeyedLock<uint> castagnoliHashLock,
            KeyedLock<ulong> perceptualHashLock)
        {
            while (parsedImages.TryDequeue(out var parsedImage))
            {
                var (sourceUri, previewUri) = parsedImage;
                using var previewResponse = await GetWithRetry(previewUri ?? sourceUri);
                if (!previewResponse.IsSuccessStatusCode)
                {
                    Log.Error(
                        $"Skipping download of {sourceUri} because the preview {previewUri ?? sourceUri} returned status code {previewResponse.StatusCode}.");
                    continue;
                }

                var previewResponseBytes = await previewResponse.Content.ReadAsByteArrayAsync();

                var castagnoliHash = Crc32CAlgorithm.Compute(previewResponseBytes);
                using var lock1 = await castagnoliHashLock.WaitAsync(castagnoliHash);
                if (!castagnoliHashes.TryAdd(castagnoliHash, true))
                {
                    Log.Debug(
                        $"Duplicate image ignored because it had the same Castagnoli hash ({castagnoliHash}) as a previously-downloaded image: {sourceUri}");
                    duplicates.Increment();
                    continue;
                }
                var perceptualHash = PerceptualHash(previewResponseBytes);
                using var lock2 = await perceptualHashLock.WaitAsync(perceptualHash);
                if (perceptualHashes.ContainsKey(perceptualHash))
                {
                    Log.Debug(
                        $"Duplicate image ignored because it had the same perceptual hash ({perceptualHash}) as a previously-downloaded image: {sourceUri}");
                    duplicates.Increment();
                    continue;
                }

                if (perceptualDuplicateHashes.ContainsKey(perceptualHash))
                {
                    Log.Debug(
                        $"Duplicate image ignored because it had the same perceptual hash ({perceptualHash}) as a previous near-duplicate that was ignored: {sourceUri}");
                    duplicates.Increment();
                    continue;
                }

                if (IsPerceptualDuplicate(perceptualHash, perceptualHashes.Keys, out var hammingDistance,
                    out var perceptualDuplicateOf))
                {
                    perceptualDuplicateHashes.TryAdd(perceptualHash, true);
                    Log.Debug(
                        $"Near-duplicate image ignored because it was within Hamming distance {hammingDistance} of a previously-downloaded image with perceptual hash {perceptualDuplicateOf}: {sourceUri}");
                    duplicates.Increment();
                    continue;
                }

                perceptualHashes.TryAdd(perceptualHash, true);

                var filenameWithoutExtension = $"{perceptualHash} {castagnoliHash}";
                if (!filenamesWithoutExtensions.TryAdd(filenameWithoutExtension, true))
                {
                    Log.Debug(
                        $"Skipping download because {filenameWithoutExtension}.* is already present in {authorDirectory.FullName}.");
                    continue;
                }

                byte[]? sourceResponseBytes;
                string? extension;
                if (previewUri is not null)
                {
                    using var sourceResponse = await GetWithRetry(sourceUri);
                    if (!sourceResponse.IsSuccessStatusCode)
                    {
                        Log.Error($"Failed to download {sourceUri}, status code {sourceResponse.StatusCode}");
                        castagnoliHashes.TryRemove(castagnoliHash, out _);
                        perceptualHashes.TryRemove(perceptualHash, out _);
                        filenamesWithoutExtensions.TryRemove(filenameWithoutExtension, out _);
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
                Log.Information($"Saved {sourceUri} to {filePath}");
            }
        }

        private static async Task<HttpResponseMessage> GetWithRetry(Uri uri)
        {
            var count = 1;
            while (true)
            {
                var response = await HttpClient.GetAsync(uri);
                if ((int) response.StatusCode < 500 || count >= 3)
                {
                    return response;
                }
                Log.Warning($"Request to {uri} failed with status code {response.StatusCode}. Attempting retry. Response body: {await response.Content.ReadAsStringAsync()}");
                count++;
            }
        }

        private static Appsettings LoadAppsettings()
        {
            var json = File.ReadAllText(Path.Combine(
                Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!,
                "appsettings.json"));
            var options = new JsonSerializerOptions
            {
                ReadCommentHandling = JsonCommentHandling.Skip,
                Converters =
                {
                    new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
                }
            };
            var (downloadDirectory, maxHammingDistance, maxParallelism, minimumLogEventLevel) = JsonSerializer.Deserialize<RawAppsettings>(json, options)!;
            if (maxParallelism == 0)
            {
                throw new Exception("MaxParallism must be greater than 0.");
            }
            return new Appsettings(
                new DirectoryInfo(downloadDirectory ?? throw new ArgumentNullException(nameof(downloadDirectory))),
                maxHammingDistance ?? throw new ArgumentNullException(nameof(maxHammingDistance)),
                maxParallelism ?? throw new ArgumentNullException(nameof(maxParallelism)),
                minimumLogEventLevel ?? throw new ArgumentNullException(nameof(minimumLogEventLevel)));
        }

        private static string GetFileExtension(HttpResponseMessage httpResponseMessage) =>
            httpResponseMessage.Content.Headers.TryGetValues("Content-Type", out var values)
                ? MimeTypeMap.GetExtension(values.Single())
                : ".jpg";

        private static bool IsPerceptualDuplicate(
            ulong perceptualHash,
            IEnumerable<ulong> perceptualHashes,
            out int? hammingDistance,
            out ulong? perceptualDuplicateOf)
        {
            if (Appsettings.MaxHammingDistance == 0)
            {
                hammingDistance = null;
                perceptualDuplicateOf = null;
                return false;
            }

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
            Log.Information($"Getting submissions after {DateTimeOffset.FromUnixTimeSeconds(createdAfter)}.");
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
                    Log.Debug($"Found no images in post {submission.Uri}");
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
                Log.Warning($"Rejecting an invalid media from post {submissionUri} because it has no source image: {JsonSerializer.Serialize(imageMetadata)}");
                return null;
            }

            // Note: Reddit sometimes returns image URLs with HTML encoded ampersands, like https://preview.redd.it/yp20vnw3fdw61.jpg?width=108&amp;crop=smart&amp;auto=webp&amp;s=31797b476190709566e62deb9a26ddc5e2ee3f58, so this fixes those URLs.
            var sourceUri = new Uri(WebUtility.HtmlDecode(imageMetadata.Source.Uri.ToString()));

            if (imageMetadata is MediaMetadata mediaMetadata && mediaMetadata.MediaType != "Image")
            {
                Log.Warning($"Rejecting {sourceUri} from post {submissionUri} because its media type was {mediaMetadata.MediaType} instead of \"Image\".");
                return null;
            }

            var smallestPreview = imageMetadata.Previews
                ?.Where(p => p.Height * p.Width > 0)
                .OrderBy(p => p.Height * p.Width)
                .FirstOrDefault()
                ?.Uri;
            if (smallestPreview is null)
            {
                Log.Warning($"An image in post {submissionUri} does not have a preview image. Falling back to using the source image {sourceUri} instead. This may reduce performance. If you see this warning constantly, file a bug.");
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
                Log.Information($"Resuming from checkpoint timestamp {DateTimeOffset.FromUnixTimeSeconds(checkpoint.NewestCreated)}");
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

        private static Uri GetUriFor(string author, int createdAfter) =>
            new($"https://api.pushshift.io/reddit/search/submission?author={author}&after={createdAfter}&size=100");

        private static ConcurrentDictionary<TKey, bool> ToConcurrentDictionary<TKey>(this IEnumerable<TKey> keys) where TKey : notnull =>
            new(keys.Select(key => KeyValuePair.Create(key, true)));
    }

    internal record Checkpoint(
        int NewestCreated,
        IEnumerable<uint> CastagnoliHashes,
        IEnumerable<ulong> PerceptualHashes,
        IEnumerable<ulong> PerceptualDuplicateHashes,
        int DuplicateCount);

    internal record Appsettings(
        DirectoryInfo DownloadDirectory,
        ushort MaxHammingDistance,
        ushort MaxParallelism,
        LogEventLevel MinimumLogEventLevel);

    internal record RawAppsettings(
        string? DownloadDirectory,
        ushort? MaxHammingDistance,
        ushort? MaxParallelism,
        LogEventLevel? MinimumLogEventLevel);

    internal record ParsedSubmissionResponse(
        IReadOnlyList<ParsedImage> ParsedImages,
        int NewestCreated);

    internal record ParsedImage(
        Uri SourceUri,
        Uri? PreviewUri);

    internal class Duplicates
    {
        private int _count;
        public int Count =>
            _count;

        public Duplicates(int count) =>
            _count = count;

        public void Increment() =>
            Interlocked.Increment(ref _count);
    }
}