using System.Windows.Media;
using System.Windows.Media.Imaging;
using EstreamAssistant.Core.Receipts;

namespace EstreamAssistant.App.Services;

/// <summary>
/// F5.2: downscales receipt photos to about 1600 px on the long edge with Windows imaging,
/// and turns phone photos upright (EXIF orientation) so Claude reads them the right way up.
/// </summary>
public sealed class WpfImageShrinker : IImageShrinker
{
    public (byte[] Data, string MediaType)? Shrink(byte[] data, string mediaType, int maxEdge)
    {
        BitmapFrame frame;
        try
        {
            using var ms = new MemoryStream(data);
            var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            frame = decoder.Frames[0];
        }
        catch (Exception) when (mediaType is "image/webp" or "image/gif" && data.Length <= ReceiptIngest.MaxImageBytes)
        {
            // No codec on this PC (WebP needs the Windows extension): Claude can still read the original.
            return null;
        }

        int w = frame.PixelWidth, h = frame.PixelHeight;
        var rotate = Orientation(frame);
        double scale = Math.Min(1.0, maxEdge / (double)Math.Max(w, h));
        bool tooBig = data.Length > ReceiptIngest.MaxImageBytes;
        if (scale >= 1.0 && rotate == 0 && !tooBig) return null;

        BitmapSource src = frame;
        var tg = new TransformGroup();
        if (scale < 1.0) tg.Children.Add(new ScaleTransform(scale, scale));
        if (rotate != 0) tg.Children.Add(new RotateTransform(rotate));
        if (tg.Children.Count > 0) src = new TransformedBitmap(src, tg);

        // Screenshots stay PNG (sharp text) unless that's still too big; photos become JPEG.
        if (mediaType == "image/png")
        {
            var png = Encode(new PngBitmapEncoder(), src);
            if (png.Length <= ReceiptIngest.MaxImageBytes) return (png, "image/png");
        }
        var opaque = new FormatConvertedBitmap(src, PixelFormats.Bgr24, null, 0);
        return (Encode(new JpegBitmapEncoder { QualityLevel = 85 }, opaque), "image/jpeg");
    }

    private static byte[] Encode(BitmapEncoder encoder, BitmapSource src)
    {
        encoder.Frames.Add(BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    /// <summary>Degrees to turn the picture so it's upright, from the EXIF orientation tag.</summary>
    private static double Orientation(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata md && md.ContainsQuery("/app1/ifd/{ushort=274}")
                && md.GetQuery("/app1/ifd/{ushort=274}") is ushort o)
                return o switch { 3 => 180, 6 => 90, 8 => 270, _ => 0 };
        }
        catch (Exception)
        {
            // Odd metadata: leave the picture as it is.
        }
        return 0;
    }

    /// <summary>A small preview for the chat and the attachment tray (null for PDFs or unreadable pictures).</summary>
    public static ImageSource? Thumbnail(ReceiptFile f, int width = 96)
    {
        if (f.Kind == ReceiptKind.Pdf) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = width;
            bmp.StreamSource = new MemoryStream(f.Data);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
