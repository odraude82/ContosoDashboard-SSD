using System.IO.Compression;
using System.Text;
using ContosoDashboard.Options;
using ContosoDashboard.Services;
using Xunit;

namespace ContosoDashboard.Tests.Unit;

public sealed class DocumentFileValidationTests
{
    private readonly DocumentFileValidator validator = new(new DocumentStorageOptions());

    [Theory]
    [InlineData("report.pdf", "application/pdf")]
    [InlineData("report.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [InlineData("report.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")]
    [InlineData("report.pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation")]
    [InlineData("report.txt", "text/plain")]
    [InlineData("report.jpg", "image/jpeg")]
    [InlineData("report.png", "image/png")]
    public async Task AcceptsSupportedFormatsWithMatchingContent(string fileName, string expectedContentType)
    {
        var content = CreateValidContent(fileName);

        var result = await validator.ValidateAsync(fileName, new MemoryStream(content), content.LongLength);

        Assert.True(result.IsValid, result.Error);
        Assert.Equal(expectedContentType, result.ContentType);
    }

    [Theory]
    [InlineData("legacy.doc")]
    [InlineData("legacy.xls")]
    [InlineData("legacy.ppt")]
    public async Task RejectsLegacyOfficeFormats(string fileName)
    {
        var oleSignature = new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };

        var result = await validator.ValidateAsync(fileName, new MemoryStream(oleSignature), oleSignature.Length);

        Assert.False(result.IsValid);
    }

    [Theory]
    [InlineData("report.pdf", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })]
    [InlineData("report.png", new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D })]
    [InlineData("report.jpg", new byte[] { 0x00, 0x01, 0x02 })]
    public async Task RejectsMismatchedContentSignatures(string fileName, byte[] content)
    {
        var result = await validator.ValidateAsync(fileName, new MemoryStream(content), content.LongLength);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task RejectsFilesLargerThanConfiguredLimitBeforeReadingContent()
    {
        var result = await validator.ValidateAsync(
            "report.pdf",
            new MemoryStream(),
            DocumentStorageOptions.MaximumAllowedFileSizeBytes + 1);

        Assert.False(result.IsValid);
        Assert.Contains("25", result.Error, StringComparison.Ordinal);
    }

    private static byte[] CreateValidContent(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return extension switch
        {
            ".pdf" => Encoding.ASCII.GetBytes("%PDF-1.7\n"),
            ".docx" => CreateOfficePackage("word/document.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"),
            ".xlsx" => CreateOfficePackage("xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"),
            ".pptx" => CreateOfficePackage("ppt/presentation.xml", "application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml"),
            ".txt" => Encoding.UTF8.GetBytes("Plain text document\n"),
            ".jpg" => [0xFF, 0xD8, 0xFF, 0xD9],
            ".png" => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A],
            _ => throw new ArgumentOutOfRangeException(nameof(fileName))
        };
    }

    private static byte[] CreateOfficePackage(string mainPartPath, string mainContentType)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var contentTypes = archive.CreateEntry("[Content_Types].xml");
            using (var writer = new StreamWriter(contentTypes.Open(), Encoding.UTF8))
            {
                writer.Write($"<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Override PartName=\"/{mainPartPath}\" ContentType=\"{mainContentType}\" /></Types>");
            }

            using var mainPart = new StreamWriter(archive.CreateEntry(mainPartPath).Open(), Encoding.UTF8);
            mainPart.Write("<document />");
        }

        return output.ToArray();
    }
}