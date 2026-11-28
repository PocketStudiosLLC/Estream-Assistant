using System.Windows.Media;
using System.Windows.Media.Imaging;
using EstreamAssistant.Core.Receipts;

namespace EstreamAssistant.App.Services;

/// <summary>
/// The pages of a receipt PDF as pictures for the Pending tab, so it shows in the receipt box instead of a separate
/// viewer: a scanner's PDF gives its scans straight out (JPEG or fax-style), anything else is drawn by Windows'
/// own PDF renderer (Windows 10 and later).
/// </summary>
public static class PdfPictures
{
    /// <summary>Each page as a picture (frozen, safe to hand to the UI thread), or empty when it can't be shown.</summary>
    public static async Task<IReadOnlyList<ImageSource>> PagesAsync(string path, Action<string, Exception>? log = null)
    {
        byte[] bytes;
        try { bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"Reading {path} failed", ex);
            return [];
        }

        if (ScannedPdf.Pages(bytes) is { } scans)
        {
            try { return scans.Select(Decode).ToList(); }
            catch (Exception ex) { log?.Invoke($"Showing the scans in {Path.GetFileName(path)} failed", ex); }
        }

        try { return await RenderAsync(path).ConfigureAwait(false); }
        catch (Exception ex)
        {
            log?.Invoke($"Drawing {Path.GetFileName(path)} with Windows' PDF renderer failed", ex);
            return [];
        }
    }

    private static ImageSource Decode(byte[] picture)
    {
        using var ms = new MemoryStream(picture);
        var frame = BitmapDecoder.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        frame.Freeze();
        return frame;
    }

    private static async Task<IReadOnlyList<ImageSource>> RenderAsync(string path)
    {
        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
        var doc = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file);
        var pages = new List<ImageSource>();
        for (uint i = 0; i < Math.Min(doc.PageCount, 20u); i++)
        {
            using var page = doc.GetPage(i);
            using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            // About 1400 px wide: sharp enough to read typed lines, small enough to scroll through.
            var width = (uint)Math.Clamp(page.Size.Width * 2, 800, 1400);
            await page.RenderToStreamAsync(stream, new Windows.Data.Pdf.PdfPageRenderOptions { DestinationWidth = width });
            using var net = stream.AsStream();
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = net;
            bmp.EndInit();
            bmp.Freeze();
            pages.Add(bmp);
        }
        return pages;
    }
}
