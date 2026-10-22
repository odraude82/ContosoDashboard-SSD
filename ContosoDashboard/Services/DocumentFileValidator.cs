using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using ContosoDashboard.Options;

namespace ContosoDashboard.Services;

public sealed class DocumentFileValidator
{
    private const int MaximumPdfHeaderOffset = 1024;
    private const long MaximumContentTypesXmlBytes = 1_048_576;
    private readonly DocumentStorageOptions options;

    public DocumentFileValidator(DocumentStorageOptions options)
    {
        this.options = options;
    }

    public async Task<DocumentFileValidationResult> ValidateAsync(
        string fileName,
        Stream content,
        long fileSizeBytes,
        CancellationToken cancellationToken = default)
    {
        if (fileSizeBytes <= 0)
        {
            return DocumentFileValidationResult.Invalid("The file is empty.");
        }

        if (fileSizeBytes > options.MaxFileSizeBytes)
        {
            return DocumentFileValidationResult.Invalid("The file exceeds the 25 MB per-file limit.");
        }

        if (!content.CanRead || !content.CanSeek)
        {
            return DocumentFileValidationResult.Invalid("The staged file could not be read for validation.");
        }

        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (!TryGetSupportedContentType(extension, out var contentType))
        {
            return DocumentFileValidationResult.Invalid("The selected file type is not supported.");
        }

        if (content.Length != fileSizeBytes)
        {
            return DocumentFileValidationResult.Invalid("The staged file size does not match the reported file size.");
        }

        var originalPosition = content.Position;
        try
        {
            content.Position = 0;
            var isValid = extension switch
            {
                ".pdf" => await HasPdfSignatureAsync(content, cancellationToken),
                ".docx" => await HasOfficePackageAsync(
                    content,
                    "word/document.xml",
                    "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml",
                    cancellationToken),
                ".xlsx" => await HasOfficePackageAsync(
                    content,
                    "xl/workbook.xml",
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml",
                    cancellationToken),
                ".pptx" => await HasOfficePackageAsync(
                    content,
                    "ppt/presentation.xml",
                    "application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml",
                    cancellationToken),
                ".txt" => await IsUtf8TextAsync(content, cancellationToken),
                ".jpg" or ".jpeg" => await HasSignatureAsync(content, [0xFF, 0xD8, 0xFF], cancellationToken),
                ".png" => await HasSignatureAsync(content, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], cancellationToken),
                _ => false
            };

            return isValid
                ? DocumentFileValidationResult.Valid(contentType)
                : DocumentFileValidationResult.Invalid("The file content does not match its supported file type.");
        }
        catch (DecoderFallbackException)
        {
            return DocumentFileValidationResult.Invalid("Plain text files must contain valid UTF-8 text.");
        }
        catch (Exception exception) when (exception is InvalidDataException or XmlException or IOException)
        {
            return DocumentFileValidationResult.Invalid("The file content is malformed or could not be validated.");
        }
        finally
        {
            content.Position = originalPosition;
        }
    }

    private static bool TryGetSupportedContentType(string extension, out string contentType)
    {
        contentType = extension switch
        {
            ".pdf" => "application/pdf",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            ".txt" => "text/plain",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            _ => string.Empty
        };

        return contentType.Length > 0;
    }

    private static async Task<bool> HasPdfSignatureAsync(Stream content, CancellationToken cancellationToken)
    {
        var header = new byte[MaximumPdfHeaderOffset + 5];
        var bytesRead = await ReadUpToAsync(content, header, cancellationToken);
        var signature = "%PDF-"u8;

        for (var offset = 0; offset <= Math.Min(MaximumPdfHeaderOffset, bytesRead - signature.Length); offset++)
        {
            if (header.AsSpan(offset, signature.Length).SequenceEqual(signature))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<bool> HasSignatureAsync(
        Stream content,
        byte[] signature,
        CancellationToken cancellationToken)
    {
        var header = new byte[signature.Length];
        var bytesRead = await ReadUpToAsync(content, header, cancellationToken);
        return bytesRead == signature.Length && header.AsSpan().SequenceEqual(signature);
    }

    private static async Task<int> ReadUpToAsync(
        Stream content,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var bytesRead = await content.ReadAsync(buffer[totalRead..], cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }

            totalRead += bytesRead;
        }

        return totalRead;
    }

    private static async Task<bool> IsUtf8TextAsync(Stream content, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(
            content,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096,
            leaveOpen: true);
        var buffer = new char[4096];
        int charactersRead;
        while ((charactersRead = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            if (Array.IndexOf(buffer, '\0', 0, charactersRead) >= 0)
            {
                return false;
            }
        }

        return true;
    }

    private static async Task<bool> HasOfficePackageAsync(
        Stream content,
        string mainPartPath,
        string expectedContentType,
        CancellationToken cancellationToken)
    {
        using var archive = new ZipArchive(content, ZipArchiveMode.Read, leaveOpen: true);
        var contentTypesEntry = archive.GetEntry("[Content_Types].xml");
        if (contentTypesEntry is null
            || contentTypesEntry.Length > MaximumContentTypesXmlBytes
            || archive.GetEntry(mainPartPath) is null)
        {
            return false;
        }

        await using var contentTypesStream = contentTypesEntry.Open();
        var settings = new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumContentTypesXmlBytes
        };
        using var xmlReader = XmlReader.Create(contentTypesStream, settings);
        var document = await XDocument.LoadAsync(xmlReader, LoadOptions.None, cancellationToken);

        return document.Root?.Elements().Any(element =>
            element.Name.LocalName == "Override"
            && string.Equals(element.Attribute("PartName")?.Value, $"/{mainPartPath}", StringComparison.Ordinal)
            && string.Equals(element.Attribute("ContentType")?.Value, expectedContentType, StringComparison.Ordinal)) == true;
    }
}

public sealed record DocumentFileValidationResult(bool IsValid, string? ContentType, string? Error)
{
    public static DocumentFileValidationResult Valid(string contentType) => new(true, contentType, null);

    public static DocumentFileValidationResult Invalid(string error) => new(false, null, error);
}