// SPDX-License-Identifier: MIT
using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using David2Exchange.Output;

namespace David2Exchange.Tests.Output;

public sealed class FaxPdfConverterTests : IDisposable
{
    private const double A4Width = 595.28;
    private const double A4Height = 841.89;

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "d2e-fax-" + Guid.NewGuid().ToString("N"));

    public FaxPdfConverterTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Standard_resolution_fax_fills_the_page_instead_of_being_squashed()
    {
        // 1728 x 1143 Pixel bei 204 x 98 dpi sind physisch ungefähr eine A4-Seite.
        var placement = FaxPdfConverter.FitToPage(1728, 1143, 204, 98, A4Width, A4Height);

        Assert.InRange(placement.Height, 800, A4Height);
        Assert.InRange(placement.Width, 570, A4Width);
    }

    [Fact]
    public void Fine_resolution_fax_keeps_its_aspect_ratio()
    {
        var placement = FaxPdfConverter.FitToPage(1728, 2286, 204, 196, A4Width, A4Height);

        var expected = (2286.0 / 196) / (1728.0 / 204);
        Assert.Equal(expected, placement.Height / placement.Width, 3);
    }

    [Fact]
    public void Missing_resolution_falls_back_to_pixels()
    {
        var placement = FaxPdfConverter.FitToPage(1000, 1000, 0, 0, A4Width, A4Height);

        Assert.Equal(placement.Width, placement.Height, 3);
    }

    [Fact]
    public void Multi_page_tiff_becomes_a_pdf_with_every_page_and_its_metadata()
    {
        var output = Path.Combine(_directory, "fax.pdf");

        var ok = FaxPdfConverter.TryConvertTiffToPdf(
            CreateTiff(pages: 3),
            output,
            new FaxPdfMetadata("Titel", "Absender", "Fax 123", "David;Fax", "tool", "tool", new DateTime(2024, 1, 2)),
            out var error);

        Assert.True(ok, error);
        var pdf = Encoding.Latin1.GetString(File.ReadAllBytes(output));
        Assert.StartsWith("%PDF", pdf, StringComparison.Ordinal);
        Assert.Contains("/Count 3", pdf, StringComparison.Ordinal);
    }

    [Fact]
    public void Broken_tiff_reports_an_error_instead_of_throwing()
    {
        var ok = FaxPdfConverter.TryConvertTiffToPdf(
            new byte[] { 1, 2, 3 },
            Path.Combine(_directory, "broken.pdf"),
            new FaxPdfMetadata("", "", "", "", "", "", DateTime.Now),
            out var error);

        Assert.False(ok);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    private static byte[] CreateTiff(int pages)
    {
        var codec = ImageCodecInfo.GetImageEncoders().First(e => e.FormatID == ImageFormat.Tiff.Guid);
        using var first = new Bitmap(200, 120);
        first.SetResolution(204, 98);
        using var stream = new MemoryStream();

        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.SaveFlag, (long)EncoderValue.MultiFrame);
        first.Save(stream, codec, parameters);

        for (var i = 1; i < pages; i++)
        {
            using var next = new Bitmap(200, 120);
            next.SetResolution(204, 98);
            parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.SaveFlag, (long)EncoderValue.FrameDimensionPage);
            first.SaveAdd(next, parameters);
        }

        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.SaveFlag, (long)EncoderValue.Flush);
        first.SaveAdd(parameters);
        return stream.ToArray();
    }
}
