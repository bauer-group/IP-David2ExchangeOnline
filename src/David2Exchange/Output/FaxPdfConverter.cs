// SPDX-License-Identifier: MIT
using System.Drawing;
using System.Drawing.Imaging;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;

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
                png.Position = 0;

                var page = document.AddPage();
                page.Width = XUnit.FromMillimeter(210);
                page.Height = XUnit.FromMillimeter(297);

                using var graphics = XGraphics.FromPdfPage(page);
                using var xImage = XImage.FromStream(() => png);

                var pageWidth = page.Width.Point;
                var pageHeight = page.Height.Point;
                var scale = Math.Min(pageWidth / image.Width, pageHeight / image.Height);
                var width = image.Width * scale;
                var height = image.Height * scale;
                var x = (pageWidth - width) / 2;
                var y = (pageHeight - height) / 2;

                graphics.DrawImage(xImage, x, y, width, height);
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
}
