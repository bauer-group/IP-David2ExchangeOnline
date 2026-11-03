// SPDX-License-Identifier: MIT
using System.Drawing;
using System.Drawing.Imaging;
using MigraDocCore.DocumentObjectModel.MigraDoc.DocumentObjectModel.Shapes;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.PixelFormats;

namespace David2Exchange.Output;

internal sealed record FaxPdfMetadata(
    string Title,
    string Author,
    string Subject,
    string Keywords,
    string Creator,
    string Producer,
    DateTime Created);

internal static class FaxPdfConverter
{
    private const double PointsPerInch = 72;

    public static bool TryConvertTiffToPdf(
        byte[] tiff,
        string outputPath,
        FaxPdfMetadata metadata,
        out string? error)
    {
        error = null;

        try
        {
            using var source = new MemoryStream(tiff);
            using var image = Image.FromStream(source, useEmbeddedColorManagement: false, validateImageData: false);
            var frameCount = image.GetFrameCount(FrameDimension.Page);
            if (frameCount <= 0)
            {
                error = "TIFF enthält keine Seiten.";
                return false;
            }

            using var document = new PdfDocument();
            document.Info.Title = metadata.Title;
            document.Info.Author = metadata.Author;
            document.Info.Subject = metadata.Subject;
            document.Info.Keywords = metadata.Keywords;
            document.Info.Creator = metadata.Creator;
            document.Info.CreationDate = metadata.Created;
            document.Info.ModificationDate = metadata.Created;

            for (var frame = 0; frame < frameCount; frame++)
            {
                image.SelectActiveFrame(FrameDimension.Page, frame);

                using var png = new MemoryStream();
                image.Save(png, ImageFormat.Png);

                var page = document.AddPage();
                page.Width = XUnit.FromMillimeter(210);
                page.Height = XUnit.FromMillimeter(297);

                using var graphics = XGraphics.FromPdfPage(page);
                using var xImage = XImage.FromImageSource(new PngFrameSource(
                    "*" + Guid.NewGuid().ToString("B"),
                    png.ToArray(),
                    image.Width,
                    image.Height));

                var placement = FitToPage(
                    image.Width,
                    image.Height,
                    image.HorizontalResolution,
                    image.VerticalResolution,
                    page.Width.Point,
                    page.Height.Point);

                graphics.DrawImage(xImage, placement.X, placement.Y, placement.Width, placement.Height);
            }

            document.Save(outputPath);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Berechnet die Lage auf der Seite aus der physischen Größe. Faxe in Standardauflösung
    /// haben nicht-quadratische Pixel (204 x 98 dpi); nach Pixeln skaliert wären sie auf
    /// knapp die halbe Höhe gestaucht.
    /// </summary>
    internal static (double X, double Y, double Width, double Height) FitToPage(
        int widthPixels,
        int heightPixels,
        double dpiX,
        double dpiY,
        double pageWidth,
        double pageHeight)
    {
        var hasResolution = dpiX > 1 && dpiY > 1;
        var physicalWidth = hasResolution ? widthPixels / dpiX * PointsPerInch : widthPixels;
        var physicalHeight = hasResolution ? heightPixels / dpiY * PointsPerInch : heightPixels;

        var scale = Math.Min(pageWidth / physicalWidth, pageHeight / physicalHeight);
        var width = physicalWidth * scale;
        var height = physicalHeight * scale;
        return ((pageWidth - width) / 2, (pageHeight - height) / 2, width, height);
    }

    /// <summary>
    /// Hält je Seite nur die PNG-Daten. Der Standardweg über XImage.FromStream behält das
    /// dekodierte Bild (~16 MB je Seite) bis zum Speichern; im x86-Prozess scheiterten so schon
    /// Faxe ab etwa 45 Seiten. Dekodiert wird nur noch kurz beim Schreiben der Seite.
    /// </summary>
    private sealed class PngFrameSource : ImageSource.IImageSource
    {
        private readonly byte[] _png;

        public PngFrameSource(string name, byte[] png, int width, int height)
        {
            Name = name;
            _png = png;
            Width = width;
            Height = height;
        }

        public int Width { get; }

        public int Height { get; }

        public string Name { get; }

        // Erzwingt den verlustfreien Bitmap-Weg, wie beim PNG-Zweig von PdfSharpCore.
        public bool Transparent => true;

        public void SaveAsJpeg(MemoryStream ms)
        {
            throw new NotSupportedException();
        }

        public void SaveAsPdfBitmap(MemoryStream ms)
        {
            using var decoded = SixLabors.ImageSharp.Image.Load<Rgba32>(_png);
            decoded.Save(ms, new BmpEncoder { BitsPerPixel = BmpBitsPerPixel.Pixel32 });
        }
    }
}
