# Reddit image downloader, with duplicate filtering

You're browsing porn on Reddit. You click on an E-Girl. **"Dang, why does she post so many duplicate images?"** You spend the next two hours scrolling through her 10,000 images, of which 9,980 are duplicates. You feel cheated. Wouldn't it be great if there was a way to filter out those duplicate images?

This CLI tool downloads a Reddit user's images, skipping duplicates along the way.

## How to use

You'll need to download the tool, configure it (optional), and run it.

### Download the tool

Download and unzip [a release](https://github.com/CSharp-Truckstop-Restroom-Hero/DownloadRedditImages/releases) that matches your platform (Windows, Mac, or Linux).

### Configure the tool (optional)

The file `appsettings.json` has four configuration settings:

- `DownloadDirectory`: Default is `.`, the current directory. Subfolders with each Reddit user's name will be created here, and images will be saved in them.
- `MaxHammingDistance`: Default is 0, and must be a non-negative integer. Leaving this as 0 is fine for most purposes. The higher the number, the more aggressive the duplicate filtering will be, which can reduce the number of downloads, but increases the chances an image that you consider subjectively different from a previous download will be filtered out as a duplicate. 15 is probably too high.
- `MaxParallelism`: Default is 4. Must be at least 1. Setting higher than 1 parallelizes image fetches. Beyond about 4-8, offers no speed increase, because Pushshift API metadata fetches, which are not parallelized, become a bottleneck.
- `MinimumLogEventLevel`: Default is `Information`. Controls log verbosity. Possible values are `Verbose`, `Debug`, `Information`, `Warning`, `Error`, and `Fatal`.

### Run the tool

Pass 1 or more Reddit usernames as arguments to the executable, separated by spaces. In this example, I use [this user](https://www.reddit.com/user/dmishin/):

```
DownloadRedditImages.exe dmishin
```

It will download images to the directory `{DownloadDirectory}/{username}`, creating it if it does not already exist.

If you run the tool again, it will intelligently skip re-downloading images that were already downloaded, and only download new images since the last run.

There is also a convenience `all` argument to run for all users in the download directory, allowing for easy updates:

```
DownloadRedditImages.exe all
```

## How it works

The tool quickly filters out duplicate downloads by first downloading "preview" images (which are very small, typically 108x108 pixels), and then passes the preview images through a 2-layered hierarchical hash filter:

- A CRC-32C "Castagnoli" hash, which simply filters out duplicates with the exact same file content, and
- A [perceptual hash](http://phash.org/), which filters out duplicates with near-identical image visual features. The perceptual hash filter "aggressiveness" can be tuned with the `MaxHammingDistance` config setting, as described earlier.

Images which pass both hash filters are downloaded in full size. The image file name convention is `"{perceptual hash} {castagnoli hash}.jpg"`.

A `checkpoint.json` file is also written to the folder containing the images, and keeps track of the progress of the downloads.

## Comparison with other tools

This tool only downloads user images. If you need more than just user image downloads, there's lots of other great choices:

- https://github.com/aliparlakci/bulk-downloader-for-reddit: Limited to 1000 most recent submissions, no perceptual duplicate filtering support (only file content duplicate filtering).
- https://github.com/MalloyDelacroix/DownloaderForReddit: Limited to 1000 most recent submissions, no perceptual or file content duplicate filtering.
- https://github.com/p-ranav/saveddit: Requires OAuth account credential setup, probably limited to 1000 most recent submissions (?), no perceptual or file content duplicate filtering.
- https://github.com/shadowmoose/RedditDownloader: Does everything this tool does, and more. Uses a slightly lower-quality perceptual duplicate filter ([difference hash](http://www.hackerfactor.com/blog/index.php?/archives/529-Kind-of-Like-That.html)) than this tool (DCT-based pHash).

## Troubleshooting

- **On Linux, I get an error saying `Unable to load DLL 'libgdiplus'`**: Run `apt-get install -y libgdiplus`, as described [here](https://github.com/dotnet/core/issues/2746#issuecomment-595980412), and try again.
- **A user just posted a new image. I ran the tool, but the new image wasn't downloaded. Why?**: Reddit user posts are retrieved using Pushshift API, which lags behind live Reddit data by a few hours. Wait and try again?
- **An error happened while downloading and the tool crashed. What do I do?**: Run the tool again. It will resume where it left off. If it dies repeatedly, file a bug.
- **Does this download videos?**: No.
- **Pushshift API is down. This tool no longer works. What do I do?**: `git commit -m suicide`

## Publishing a new release

Requires the .NET 5 SDK. Run this:

```
dotnet publish -c Release -p:PublishTrimmed=true --runtime win-x64
dotnet publish -c Release -p:PublishTrimmed=true --runtime osx-x64
dotnet publish -c Release -p:PublishTrimmed=true --runtime linux-x64
```

Output will be in `DownloadRedditImages\bin\Release\net5.0\win-x64\publish` (and similar for the Linux and Mac builds). Zip the folders, and upload.
