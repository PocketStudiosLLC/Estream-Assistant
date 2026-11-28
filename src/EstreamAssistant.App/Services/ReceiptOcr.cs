using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EstreamAssistant.Core.Receipts;

namespace EstreamAssistant.App.Services;

/// <summary>
/// Reads the lines of text on receipt pictures with Windows' own text reader (Windows.Media.Ocr, part of Windows 10
/// and later, nothing sent anywhere), with where each line is, so rows Claude didn't place can still be highlighted.
/// </summary>
public static class ReceiptOcr
{
    // Some overlap between the slices a tall receipt is read in, so no line is cut in half.
    private const int Overlap = 200;

    /// <summary>The text lines on each picture (1-based picture numbers), or empty when Windows can't read them.</summary>
    public static async Task<IReadOnlyList<OcrLine>> ReadAsync(IReadOnlyList<ImageSource?> pictures, Action<string, Exception?>? log = null)
    {
        var lines = new List<OcrLine>();
        try
        {
            var engine = Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"))
                         ?? Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine is null)
            {
                log?.Invoke("Windows has no text reader (OCR) language installed, so receipt rows can't be found on the picture", null);
                return [];
            }
            int max = (int)Windows.Media.Ocr.OcrEngine.MaxImageDimension;
            for (int n = 0; n < pictures.Count; n++)
            {
                if (pictures[n] is not BitmapSource picture) continue;
                BitmapSource src = picture.PixelWidth > max
                    ? new TransformedBitmap(picture, new ScaleTransform((double)max / picture.PixelWidth, (double)max / picture.PixelWidth))
                    : picture;
                int w = src.PixelWidth, h = src.PixelHeight;
                int slice = Math.Min(h, max - 100);
                for (int y0 = 0; ; y0 += slice - Overlap)
                {
                    int sh = Math.Min(slice, h - y0);
                    var bgra = new FormatConvertedBitmap(new CroppedBitmap(src, new Int32Rect(0, y0, w, sh)), PixelFormats.Bgra32, null, 0);
                    var bytes = new byte[w * 4 * sh];
                    bgra.CopyPixels(bytes, w * 4, 0);
                    using var bitmap = Windows.Graphics.Imaging.SoftwareBitmap.CreateCopyFromBuffer(
                        bytes.AsBuffer(), Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, w, sh);
                    var result = await engine.RecognizeAsync(bitmap);
                    bool first = y0 == 0, last = y0 + sh >= h;
                    foreach (var line in result.Lines)
                    {
                        if (line.Words.Count == 0) continue;
                        double top = line.Words.Min(x => x.BoundingRect.Y) + y0;
                        double bottom = line.Words.Max(x => x.BoundingRect.Y + x.BoundingRect.Height) + y0;
                        double middle = (top + bottom) / 2;
                        // Each line once: from the slice where it's clear of the overlap.
                        if ((!first && middle < y0 + Overlap / 2) || (!last && middle >= y0 + sh - Overlap / 2)) continue;
                        lines.Add(new OcrLine(n + 1, line.Text, top * 100 / h, bottom * 100 / h));
                    }
                    if (last) break;
                }
            }
        }
        catch (Exception ex)
        {
            log?.Invoke("Reading the receipt picture with Windows' text reader failed", ex);
        }
        return lines;
    }
}
