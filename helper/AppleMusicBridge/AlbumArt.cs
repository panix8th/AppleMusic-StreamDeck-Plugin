using System.Security.Cryptography;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace AppleMusicBridge;

/// <param name="Id">Short hash of the PNG; changes when the cover changes.</param>
internal sealed record AlbumArtImage(string Id, string Base64, byte[] Png);

/// <summary>Turns the media session's thumbnail into a 144x144 PNG (the size of a Stream Deck key @2x).</summary>
internal static class AlbumArt
{
    public const int Size = 144;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>Returns null when there is no thumbnail or it cannot be decoded (logged as a warning).</summary>
    public static async Task<AlbumArtImage?> TryCreateAsync(IRandomAccessStreamReference? thumbnail, CancellationToken cancellationToken)
    {
        if (thumbnail is null)
        {
            return null;
        }

        try
        {
            using var source = await thumbnail.OpenReadAsync().AsTask(cancellationToken).WaitAsync(Timeout, cancellationToken);
            var png = await ResizeToPngAsync(source, Size).WaitAsync(Timeout, cancellationToken);
            var id = Convert.ToHexString(SHA256.HashData(png), 0, 8).ToLowerInvariant();
            return new AlbumArtImage(id, Convert.ToBase64String(png), png);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read the album art: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static async Task<byte[]> ResizeToPngAsync(IRandomAccessStream source, int size)
    {
        var decoder = await BitmapDecoder.CreateAsync(source);
        var width = decoder.PixelWidth;
        var height = decoder.PixelHeight;
        if (width == 0 || height == 0)
        {
            throw new InvalidDataException("The thumbnail has no pixels.");
        }

        // Scale the shorter side to `size`, then crop the centre square (covers are usually square already).
        var scale = (double)size / Math.Min(width, height);
        var scaledWidth = Math.Max((uint)size, (uint)Math.Round(width * scale));
        var scaledHeight = Math.Max((uint)size, (uint)Math.Round(height * scale));
        var transform = new BitmapTransform
        {
            ScaledWidth = scaledWidth,
            ScaledHeight = scaledHeight,
            InterpolationMode = BitmapInterpolationMode.Fant,
            Bounds = new BitmapBounds
            {
                X = (scaledWidth - (uint)size) / 2,
                Y = (scaledHeight - (uint)size) / 2,
                Width = (uint)size,
                Height = (uint)size,
            },
        };

        var pixelData = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            transform,
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);
        var pixels = pixelData.DetachPixelData();

        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)size, (uint)size, 96, 96, pixels);
        await encoder.FlushAsync();

        var bytes = new byte[output.Size];
        using var reader = new DataReader(output.GetInputStreamAt(0));
        await reader.LoadAsync((uint)output.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }
}
