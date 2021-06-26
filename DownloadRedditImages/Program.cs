using System;
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
using System.Threading.Tasks;
using CoenM.ImageHash;
using CoenM.ImageHash.HashAlgorithms;
using Force.Crc32;
using Shipwreck.Phash;
using Shipwreck.Phash.Bitmaps;

namespace DownloadRedditImages
{
    internal static class Program
    {
        private static readonly HttpClient HttpClient = new();
        private static readonly PerceptualHash PerceptualHash = new();
        private static readonly Appsettings Appsettings = LoadAppsettings();

        private static Appsettings LoadAppsettings()
        {
            var (outputDirectory, perceptualHashSimilarityThreshold) = JsonSerializer.Deserialize<RawAppsettings>(File.ReadAllText(Path.Combine(
                Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
                "appsettings.json")));
            return new Appsettings(new DirectoryInfo(outputDirectory), perceptualHashSimilarityThreshold);
        }

        private static async Task Main(string[] args)
        {
            var hash1 = ImagePhash.ComputeDctHash(((Bitmap) System.Drawing.Image.FromFile("E:\\Media\\Porn\\Reddit\\zoom1.jpg")).ToLuminanceImage());
            var hash2 = ImagePhash.ComputeDctHash(((Bitmap) System.Drawing.Image.FromFile("E:\\Media\\Porn\\Reddit\\zoom2.jpg")).ToLuminanceImage());
            var hash3 = ImagePhash.ComputeDctHash(((Bitmap) System.Drawing.Image.FromFile("E:\\Media\\Porn\\Reddit\\zoom3.jpg")).ToLuminanceImage());
            var hash4 = ImagePhash.ComputeDctHash(((Bitmap) System.Drawing.Image.FromFile("E:\\Media\\Porn\\Reddit\\zoom4.jpg")).ToLuminanceImage());
            var hashup = ImagePhash.ComputeDctHash(((Bitmap) System.Drawing.Image.FromFile("E:\\Media\\Porn\\Reddit\\up.jpg")).ToLuminanceImage());
            var hashdown = ImagePhash.ComputeDctHash(((Bitmap) System.Drawing.Image.FromFile("E:\\Media\\Porn\\Reddit\\down.jpg")).ToLuminanceImage());
            Info($"1-2: {ImagePhash.GetHammingDistance(hash1, hash2)}");
            Info($"1-3: {ImagePhash.GetHammingDistance(hash1, hash3)}");
            Info($"1-4: {ImagePhash.GetHammingDistance(hash1, hash4)}");
            Info($"up-down: {ImagePhash.GetHammingDistance(hashup, hashdown)}");
            Info($"1-up: {ImagePhash.GetHammingDistance(hash1, hashup)}");
            Info("");

            var rhash1 = ImagePhash.ComputeDigest(((Bitmap) System.Drawing.Image.FromFile("E:\\Media\\Porn\\Reddit\\zoom1.jpg")).ToLuminanceImage());
            var rhash2 = ImagePhash.ComputeDigest(((Bitmap) System.Drawing.Image.FromFile("E:\\Media\\Porn\\Reddit\\zoom2.jpg")).ToLuminanceImage());
            var rhash3 = ImagePhash.ComputeDigest(((Bitmap) System.Drawing.Image.FromFile("E:\\Media\\Porn\\Reddit\\zoom3.jpg")).ToLuminanceImage());
            var rhash4 = ImagePhash.ComputeDigest(((Bitmap) System.Drawing.Image.FromFile("E:\\Media\\Porn\\Reddit\\zoom4.jpg")).ToLuminanceImage());
            var rhashup = ImagePhash.ComputeDigest(((Bitmap) System.Drawing.Image.FromFile("E:\\Media\\Porn\\Reddit\\up.jpg")).ToLuminanceImage());
            var rhashdown = ImagePhash.ComputeDigest(((Bitmap) System.Drawing.Image.FromFile("E:\\Media\\Porn\\Reddit\\down.jpg")).ToLuminanceImage());
            Info($"1-2: {ImagePhash.GetCrossCorrelation(rhash1, rhash2)}");
            Info($"1-3: {ImagePhash.GetCrossCorrelation(rhash1, rhash3)}");
            Info($"1-4: {ImagePhash.GetCrossCorrelation(rhash1, rhash4)}");
            Info($"up-down: {ImagePhash.GetCrossCorrelation(rhashup, rhashdown)}");
            Info($"1-up: {ImagePhash.GetCrossCorrelation(rhash1, rhashup)}");
            Info("");


            hash1 = PerceptualHash.Hash(File.OpenRead("E:\\Media\\Porn\\Reddit\\zoom1.jpg"));
            hash2 = PerceptualHash.Hash(File.OpenRead("E:\\Media\\Porn\\Reddit\\zoom2.jpg"));
            hash3 = PerceptualHash.Hash(File.OpenRead("E:\\Media\\Porn\\Reddit\\zoom3.jpg"));
            hash4 = PerceptualHash.Hash(File.OpenRead("E:\\Media\\Porn\\Reddit\\zoom4.jpg"));
            hashup = PerceptualHash.Hash(File.OpenRead("E:\\Media\\Porn\\Reddit\\up.jpg"));
            hashdown = PerceptualHash.Hash(File.OpenRead("E:\\Media\\Porn\\Reddit\\down.jpg"));
            Info($"1-2: {CompareHash.Similarity(hash1, hash2)}");
            Info($"1-3: {CompareHash.Similarity(hash1, hash3)}");
            Info($"1-4: {CompareHash.Similarity(hash1, hash4)}");
            Info($"up-down: {CompareHash.Similarity(hashup, hashdown)}");
            Info($"1-up: {CompareHash.Similarity(hash1, hashup)}");
            Info("");

            var preview = Enumerable.Repeat(File.OpenRead("E:\\Media\\Porn\\Reddit\\preview.jpg"), 10).ToList();
            var stopwatch = Stopwatch.StartNew();
            for (int i = 0; i < 10; i++)
            {
                ImagePhash.ComputeDctHash(((Bitmap) System.Drawing.Image.FromStream(preview[i])).ToLuminanceImage());
                // PerceptualHash.Hash(preview[i]);
                preview[i].Position = 0;
            }
            var elapsed = stopwatch.ElapsedMilliseconds;
            Info(elapsed.ToString());

            // var hashes = Directory.EnumerateFiles("E:\\Media\\Porn\\Reddit\\ImNotYourAverageMom", "*.jpg")
            //     .Select(f => (f, ImagePhash.ComputeDctHash(((Bitmap) System.Drawing.Image.FromFile(f)).ToLuminanceImage())))
            //     .ToList();
            // var minDistance = (1000, "", "");
            // for (int i = 0; i < hashes.Count; i++)
            // {
            //     for (int j = 0; j < i; j++)
            //     {
            //         var distance = ImagePhash.GetHammingDistance(hashes[i].Item2, hashes[j].Item2);
            //         if (distance < minDistance.Item1)
            //         {
            //             minDistance = (distance, hashes[i].f, hashes[j].f);
            //         }
            //     }
            // }
            // Info($"Minimum distance among user photos: {minDistance.Item1} {minDistance.Item2} {minDistance.Item3}");
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
                    if (!castagnoliHashes.Contains(castagnoliHash))
                    {
                        using var previewResponseStream = new MemoryStream(previewResponseBytes);
                        var perceptualHash = PerceptualHash.Hash(previewResponseStream);
                        if (!perceptualHashes.Contains(perceptualHash))
                        {
                            if (!perceptualDuplicateHashes.Contains(perceptualHash))
                            {
                                if (IsPerceptualDuplicate(perceptualHash, perceptualHashes, out var similarity, out var perceptualDuplicateOf))
                                {
                                    Info($"Near-duplicate image ignored because it had a {similarity}% perceptual hash similarity to a previously-downloaded image with perceptual hash {perceptualDuplicateOf}: {decodedSourceUri}");
                                    perceptualDuplicateHashes.Add(perceptualHash);
                                }
                                else
                                {
                                    var sourceResponse = await HttpClient.GetAsync(decodedSourceUri);
                                    if (!sourceResponse.IsSuccessStatusCode)
                                    {
                                        Error($"Failed to download {decodedPreviewUri}, status code {sourceResponse.StatusCode}");
                                    }
                                    else
                                    {
                                        var filePath = Path.Combine(authorDirectory.FullName, $"{perceptualHash} {castagnoliHash}.jpg");
                                        await File.WriteAllBytesAsync(filePath, await sourceResponse.Content.ReadAsByteArrayAsync());
                                        Info($"Saved {decodedSourceUri} to {filePath}");

                                        castagnoliHashes.Add(castagnoliHash);
                                        perceptualHashes.Add(perceptualHash);
                                    }
                                }
                            }
                            else
                            {
                                Info($"Duplicate image ignored because it had the same perceptual hash as a previous near-duplicate that was ignored: {decodedSourceUri}");
                            }
                        }
                        else
                        {
                            Info($"Duplicate image ignored because it had the same perceptual hash ({perceptualHash}) as a previously-downloaded image: {decodedSourceUri}");
                        }
                    }
                    else
                    {
                        Info($"Duplicate image ignored because it had the same Castagnoli hash ({castagnoliHash}) as a previously-downloaded image: {decodedSourceUri}");
                    }
                }
                WriteCheckpoint(author, new Checkpoint(createdAfter, castagnoliHashes, perceptualHashes, perceptualDuplicateHashes));
            }
        }

        private static bool IsPerceptualDuplicate(
            ulong perceptualHash,
            HashSet<ulong> perceptualHashes,
            out double? similarity,
            out ulong? perceptualDuplicateOf)
        {
            foreach (var existingPerceptualHash in perceptualHashes)
            {
                similarity = CompareHash.Similarity(perceptualHash, existingPerceptualHash);
                if (similarity > Appsettings.PerceptualHashSimilarityThreshold)
                {
                    perceptualDuplicateOf = existingPerceptualHash;
                    return true;
                }
            }

            similarity = null;
            perceptualDuplicateOf = null;
            return false;
        }

        private static async Task<ParsedSubmissionResponse> Get(string author, int createdAfter)
        {
            Info($"Getting submissions after {createdAfter}.");
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
                    Error($"Found no images in post {submission?.Uri}");
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
            if (mediaMetadata.Source?.Uri != null && mediaMetadata.MediaType == "Image" && mediaMetadata.ContentType ==
                "image/jpg")
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
        double PerceptualHashSimilarityThreshold);

    internal record RawAppsettings(
        string OutputDirectory,
        double PerceptualHashSimilarityThreshold);

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
        [property: JsonPropertyName("m")] string ContentType,
        [property: JsonPropertyName("p")] IReadOnlyList<Media> Previews,
        [property: JsonPropertyName("s")] Media Source);

    internal record Media(
        [property: JsonPropertyName("u")] Uri Uri,
        [property: JsonPropertyName("x")] uint Width,
        [property: JsonPropertyName("y")] uint Height);
}