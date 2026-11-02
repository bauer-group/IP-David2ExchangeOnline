// SPDX-License-Identifier: MIT
using System.Drawing;
using System.Drawing.Imaging;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace David2Exchange.Output;

internal static class FaxPdfConverter
{
    public static bool TryConvertTiffToPdf(
        byte[] tiff,
        string outputPath,
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
            for (var frame = 0; frame < frameCount; frame++)
            {
                image.SelectActiveFrame(FrameDimension.Page, frame);

                using var png = new MemoryStream();
                image.Save(png, ImageFormat.Png);
                png.Position = 0;

                var dpiX = image.HorizontalResolution > 0 ? image.HorizontalResolution : 200;
                var dpiY = image.VerticalResolution > 0 ? image.VerticalResolution : 200;
                var page = document.AddPage();
                page.Width = XUnit.FromPoint(image.Width * 72.0 / dpiX);
                page.Height = XUnit.FromPoint(image.Height * 72.0 / dpiY);

                using var graphics = XGraphics.FromPdfPage(page);
                using var xImage = XImage.FromStream(png);
                graphics.DrawImage(
                    xImage,
                    0,
                    0,
                    page.Width.Point,
                    page.Height.Point);
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
