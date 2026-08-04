using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using SkiaSharp;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Exceptions;
using UglyToad.PdfPig.Tokens;

namespace CBSSupport.Shared.Services;

public sealed record AttachmentContentValidation(
    bool Valid,
    string? DetectedMediaType,
    long Size,
    byte[] Sha256,
    string? RejectionCode,
    byte[]? CanonicalContent = null);

public static class AttachmentContentValidator
{
    public const string PdfMediaType = "application/pdf";
    public const string JpegMediaType = "image/jpeg";
    public const string PngMediaType = "image/png";
    public const string DocxMediaType =
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    public const string XlsxMediaType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private static readonly IReadOnlyDictionary<string, string> Allowed =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".pdf"] = PdfMediaType,
            [".jpg"] = JpegMediaType,
            [".jpeg"] = JpegMediaType,
            [".png"] = PngMediaType,
            [".docx"] = DocxMediaType,
            [".xlsx"] = XlsxMediaType
        };

    private static readonly HashSet<string> ExecutablePackageExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".exe", ".dll", ".com", ".scr", ".msi", ".msp", ".bat", ".cmd",
            ".ps1", ".psm1", ".js", ".jse", ".vbs", ".vbe", ".wsf", ".wsh",
            ".hta", ".html", ".htm", ".svg", ".jar", ".sh"
        };

    private static readonly HashSet<string> DisallowedPdfNames =
        new(StringComparer.Ordinal)
        {
            "JavaScript", "JS", "Launch", "RichMedia", "RichMediaContent",
            "EmbeddedFile", "EmbeddedFiles", "Filespec", "3D", "3DView", "Collection",
            "OpenAction", "AA", "AcroForm", "XFA", "SubmitForm", "ImportData",
            "Movie", "Sound", "GoToR", "GoToE"
        };

    public static bool IsAllowedDeclaration(
        string displayName,
        string mediaType,
        out string safeDisplayName)
    {
        safeDisplayName = SanitizeDisplayName(displayName);
        var extension = Path.GetExtension(safeDisplayName);
        var declared = NormalizeMediaType(mediaType);
        return safeDisplayName.Length is > 0 and <= 255
            && Allowed.TryGetValue(extension, out var expected)
            && string.Equals(expected, declared, StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<AttachmentContentValidation> ValidateAsync(
        Stream content,
        string displayName,
        string declaredMediaType,
        long maximumBytes,
        CancellationToken cancellationToken = default,
        AttachmentStructuralValidationOptions? limits = null)
    {
        limits ??= new AttachmentStructuralValidationOptions();
        var bytes = await ReadBoundedAsync(content, maximumBytes, cancellationToken);
        if (bytes is null || bytes.Length == 0)
        {
            return Invalid(bytes?.LongLength ?? 0, "size_mismatch");
        }

        var safeName = SanitizeDisplayName(displayName);
        var extension = Path.GetExtension(safeName);
        var declared = NormalizeMediaType(declaredMediaType);
        if (!Allowed.TryGetValue(extension, out var expected)
            || !string.Equals(expected, declared, StringComparison.OrdinalIgnoreCase))
        {
            return Invalid(bytes.LongLength, "content_type_mismatch", bytes);
        }

        try
        {
            var result = extension.ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => ValidateImage(bytes, JpegMediaType, limits),
                ".png" => ValidateImage(bytes, PngMediaType, limits),
                ".docx" => ValidateOffice(bytes, wordProcessing: true, limits),
                ".xlsx" => ValidateOffice(bytes, wordProcessing: false, limits),
                ".pdf" => ValidatePdf(bytes, limits),
                _ => Invalid(bytes.LongLength, "content_type_mismatch", bytes)
            };
            return result.Valid && result.Size > maximumBytes
                ? Invalid(result.Size, "size_mismatch", result.CanonicalContent)
                : result;
        }
        catch (PdfDocumentEncryptedException)
        {
            return Invalid(bytes.LongLength, "encrypted_content", bytes);
        }
        catch (PdfDocumentFormatException)
        {
            return Invalid(bytes.LongLength, "malformed_content", bytes);
        }
        catch (Exception exception) when (
            exception is InvalidDataException
                or IOException
                or XmlException
                or OpenXmlPackageException
                or ArgumentException
                or InvalidOperationException)
        {
            return Invalid(bytes.LongLength, "malformed_content", bytes);
        }
    }

    public static string SanitizeDisplayName(string value)
    {
        var fileName = Path.GetFileName((value ?? string.Empty).Trim());
        var filtered = string.Concat(fileName.Where(character =>
            !char.IsControl(character)
            && character is not '/' and not '\\' and not '\0'));
        return filtered.Length <= 255 ? filtered : filtered[..255];
    }

    public static bool RequiresAttachmentDisposition(string mediaType) =>
        NormalizeMediaType(mediaType) is PdfMediaType or DocxMediaType or XlsxMediaType;

    private static AttachmentContentValidation ValidateImage(
        byte[] bytes,
        string expectedMediaType,
        AttachmentStructuralValidationOptions limits)
    {
        using var codec = SKCodec.Create(new MemoryStream(bytes, writable: false));
        if (codec is null || codec.FrameCount > 1)
        {
            return Invalid(bytes.LongLength, "malformed_content", bytes);
        }
        var actualMediaType = codec.EncodedFormat switch
        {
            SKEncodedImageFormat.Jpeg => JpegMediaType,
            SKEncodedImageFormat.Png => PngMediaType,
            _ => null
        };
        if (!string.Equals(actualMediaType, expectedMediaType, StringComparison.Ordinal))
        {
            return Invalid(bytes.LongLength, "content_type_mismatch", bytes);
        }

        var width = codec.Info.Width;
        var height = codec.Info.Height;
        var pixels = checked((long)width * height);
        var decodedBytes = checked(pixels * 4);
        if (width < 1 || height < 1
            || width > limits.MaximumImageWidth
            || height > limits.MaximumImageHeight
            || pixels > limits.MaximumImagePixels
            || decodedBytes > limits.MaximumDecodedImageBytes)
        {
            return Invalid(bytes.LongLength, "image_limit_exceeded", bytes);
        }

        using var bitmap = SKBitmap.Decode(bytes);
        if (bitmap is null || bitmap.Width != width || bitmap.Height != height)
        {
            return Invalid(bytes.LongLength, "malformed_content", bytes);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(
            expectedMediaType == JpegMediaType
                ? SKEncodedImageFormat.Jpeg
                : SKEncodedImageFormat.Png,
            expectedMediaType == JpegMediaType ? 90 : 100);
        if (encoded is null)
        {
            return Invalid(bytes.LongLength, "malformed_content", bytes);
        }
        var canonical = encoded.ToArray();
        return Valid(expectedMediaType, canonical);
    }

    private static AttachmentContentValidation ValidateOffice(
        byte[] bytes,
        bool wordProcessing,
        AttachmentStructuralValidationOptions limits)
    {
        var zipResult = ValidateOfficeZip(bytes, wordProcessing, limits);
        if (zipResult is not null)
        {
            return Invalid(bytes.LongLength, zipResult, bytes);
        }

        using var stream = new MemoryStream(bytes, writable: false);
        var settings = new OpenSettings
        {
            AutoSave = false,
            MaxCharactersInPart = limits.MaximumPackageUncompressedBytes
        };
        using OpenXmlPackage package = wordProcessing
            ? WordprocessingDocument.Open(stream, false, settings)
            : SpreadsheetDocument.Open(stream, false, settings);

        if (wordProcessing)
        {
            var document = (WordprocessingDocument)package;
            if (document.DocumentType != WordprocessingDocumentType.Document
                || document.MainDocumentPart?.Document is null)
            {
                return Invalid(bytes.LongLength, "malformed_content", bytes);
            }
        }
        else
        {
            var workbook = (SpreadsheetDocument)package;
            if (workbook.DocumentType != SpreadsheetDocumentType.Workbook
                || workbook.WorkbookPart?.Workbook is null)
            {
                return Invalid(bytes.LongLength, "malformed_content", bytes);
            }
        }

        if (HasUnsafeRelationshipsOrParts(package))
        {
            return Invalid(bytes.LongLength, "active_content", bytes);
        }
        var validator = new OpenXmlValidator(FileFormatVersions.Office2019);
        if (validator.Validate(package).Take(1).Any())
        {
            return Invalid(bytes.LongLength, "malformed_content", bytes);
        }
        return Valid(wordProcessing ? DocxMediaType : XlsxMediaType, bytes);
    }

    private static string? ValidateOfficeZip(
        byte[] bytes,
        bool wordProcessing,
        AttachmentStructuralValidationOptions limits)
    {
        if (bytes.Length < 4 || !bytes.AsSpan(0, 4).SequenceEqual("PK\u0003\u0004"u8))
        {
            return bytes.AsSpan().StartsWith(
                new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 })
                ? "encrypted_content"
                : "content_type_mismatch";
        }

        using var archive = new ZipArchive(
            new MemoryStream(bytes, writable: false),
            ZipArchiveMode.Read,
            leaveOpen: false);
        if (archive.Entries.Count is < 1 || archive.Entries.Count > limits.MaximumPackageEntries)
        {
            return "package_limit_exceeded";
        }
        long total = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hasContentTypes = false;
        var hasRoot = false;
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.StartsWith('/') || name.Contains("../", StringComparison.Ordinal)
                || !names.Add(name))
            {
                return "malformed_content";
            }
            total = checked(total + entry.Length);
            if (total > limits.MaximumPackageUncompressedBytes
                || (entry.Length > 0
                    && entry.Length > Math.Max(1, entry.CompressedLength)
                        * limits.MaximumPackageCompressionRatio))
            {
                return "package_limit_exceeded";
            }

            var lower = name.ToLowerInvariant();
            hasContentTypes |= lower == "[content_types].xml";
            hasRoot |= wordProcessing
                ? lower == "word/document.xml"
                : lower == "xl/workbook.xml";
            if (lower.EndsWith("vbaproject.bin", StringComparison.Ordinal)
                || lower.Contains("/embeddings/", StringComparison.Ordinal)
                || lower.Contains("/oleobjects/", StringComparison.Ordinal)
                || lower.Contains("encryptedpackage", StringComparison.Ordinal)
                || lower.Contains("encryptioninfo", StringComparison.Ordinal)
                || (!wordProcessing
                    && lower.Contains("/externallinks/", StringComparison.Ordinal))
                || ExecutablePackageExtensions.Contains(Path.GetExtension(lower)))
            {
                return lower.Contains("encrypt", StringComparison.Ordinal)
                    ? "encrypted_content"
                    : "active_content";
            }
            if (!name.EndsWith("/", StringComparison.Ordinal)
                && HasExecutableSignature(entry))
            {
                return "active_content";
            }
            if (lower.EndsWith(".rels", StringComparison.Ordinal)
                && ContainsExternalRelationship(entry, limits.MaximumPackageUncompressedBytes))
            {
                return "active_content";
            }
        }
        return hasContentTypes && hasRoot ? null : "malformed_content";
    }

    private static bool HasExecutableSignature(ZipArchiveEntry entry)
    {
        Span<byte> header = stackalloc byte[8];
        using var stream = entry.Open();
        var read = 0;
        while (read < header.Length)
        {
            var count = stream.Read(header[read..]);
            if (count == 0)
            {
                break;
            }
            read += count;
        }

        var bytes = header[..read];
        return bytes.StartsWith("MZ"u8)
            || bytes.StartsWith(new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F' })
            || bytes.StartsWith(new byte[] { 0xFE, 0xED, 0xFA, 0xCE })
            || bytes.StartsWith(new byte[] { 0xFE, 0xED, 0xFA, 0xCF })
            || bytes.StartsWith(new byte[] { 0xCE, 0xFA, 0xED, 0xFE })
            || bytes.StartsWith(new byte[] { 0xCF, 0xFA, 0xED, 0xFE })
            || bytes.StartsWith("#!"u8);
    }

    private static bool ContainsExternalRelationship(ZipArchiveEntry entry, long maxCharacters)
    {
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = maxCharacters
        });
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element
                && reader.LocalName == "Relationship"
                && string.Equals(
                    reader.GetAttribute("TargetMode"),
                    "External",
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static bool HasUnsafeRelationshipsOrParts(OpenXmlPackage package)
    {
        if (package.ExternalRelationships.Any() || package.HyperlinkRelationships.Any())
        {
            return true;
        }
        var queue = new Queue<OpenXmlPart>(package.Parts.Select(pair => pair.OpenXmlPart));
        var visited = new HashSet<OpenXmlPart>();
        while (queue.TryDequeue(out var part))
        {
            if (!visited.Add(part))
            {
                continue;
            }
            if (part is VbaProjectPart or EmbeddedObjectPart or EmbeddedPackagePart
                || part.ExternalRelationships.Any()
                || part.HyperlinkRelationships.Any())
            {
                return true;
            }
            foreach (var child in part.Parts)
            {
                queue.Enqueue(child.OpenXmlPart);
            }
        }
        return false;
    }

    private static AttachmentContentValidation ValidatePdf(
        byte[] bytes,
        AttachmentStructuralValidationOptions limits)
    {
        if (!bytes.AsSpan().StartsWith("%PDF-"u8))
        {
            return Invalid(bytes.LongLength, "content_type_mismatch", bytes);
        }
        var names = EnumeratePdfNames(bytes).ToArray();
        if (names.Contains("Encrypt", StringComparer.Ordinal))
        {
            return Invalid(bytes.LongLength, "encrypted_content", bytes);
        }
        if (names.Any(DisallowedPdfNames.Contains))
        {
            return Invalid(bytes.LongLength, "active_content", bytes);
        }
        if (CountPdfObjects(bytes) > limits.MaximumPdfObjects)
        {
            return Invalid(bytes.LongLength, "pdf_limit_exceeded", bytes);
        }

        using var document = PdfDocument.Open(bytes, ParsingOptions.LenientParsingOff);
        if (document.IsEncrypted)
        {
            return Invalid(bytes.LongLength, "encrypted_content", bytes);
        }
        if (document.NumberOfPages is < 1 || document.NumberOfPages > limits.MaximumPdfPages)
        {
            return Invalid(bytes.LongLength, "pdf_limit_exceeded", bytes);
        }
        if (document.Advanced.TryGetEmbeddedFiles(out var embedded) && embedded.Count > 0)
        {
            return Invalid(bytes.LongLength, "active_content", bytes);
        }

        var graph = InspectPdfObjectGraph(document, limits);
        if (graph.ActiveContent)
        {
            return Invalid(bytes.LongLength, "active_content", bytes);
        }
        if (graph.LimitExceeded)
        {
            return Invalid(bytes.LongLength, "pdf_limit_exceeded", bytes);
        }

        var decoded = graph.DecodedBytes;
        foreach (var page in document.GetPages())
        {
            decoded = checked(decoded
                + Encoding.UTF8.GetByteCount(page.Text)
                + (long)page.Operations.Count * 64);
            foreach (var image in page.GetImages())
            {
                decoded = checked(decoded
                    + image.RawBytes.Length
                    + (long)image.WidthInSamples * image.HeightInSamples * 4);
            }
            _ = page.GetAnnotations();
            if (decoded > limits.MaximumPdfDecodedBytes)
            {
                return Invalid(bytes.LongLength, "pdf_limit_exceeded", bytes);
            }
        }
        return Valid(PdfMediaType, bytes);
    }

    private static PdfInspection InspectPdfObjectGraph(
        PdfDocument document,
        AttachmentStructuralValidationOptions limits)
    {
        var queue = new Queue<IToken>();
        var references = new HashSet<IndirectReference>();
        queue.Enqueue(document.Structure.Catalog.CatalogDictionary);
        long decodedBytes = 0;
        while (queue.TryDequeue(out var token))
        {
            switch (token)
            {
                case NameToken name when DisallowedPdfNames.Contains(name.Data):
                    return new(true, false, decodedBytes);
                case DictionaryToken dictionary:
                    foreach (var pair in dictionary.Data)
                    {
                        if (DisallowedPdfNames.Contains(pair.Key))
                        {
                            return new(true, false, decodedBytes);
                        }
                        queue.Enqueue(pair.Value);
                    }
                    break;
                case ArrayToken array:
                    foreach (var value in array.Data)
                    {
                        queue.Enqueue(value);
                    }
                    break;
                case IndirectReferenceToken reference:
                    if (references.Add(reference.Data))
                    {
                        if (references.Count > limits.MaximumPdfObjects)
                        {
                            return new(false, true, decodedBytes);
                        }
                        queue.Enqueue(document.Structure.GetObject(reference.Data));
                    }
                    break;
                case ObjectToken value:
                    queue.Enqueue(value.Data);
                    break;
                case StreamToken stream:
                    decodedBytes = checked(decodedBytes + stream.Data.Length);
                    if (decodedBytes > limits.MaximumPdfDecodedBytes)
                    {
                        return new(false, true, decodedBytes);
                    }
                    queue.Enqueue(stream.StreamDictionary);
                    break;
            }
        }
        return new(false, false, decodedBytes);
    }

    private static IReadOnlyList<string> EnumeratePdfNames(byte[] bytes)
    {
        var names = new List<string>();
        var span = bytes.AsSpan();
        for (var index = 0; index < span.Length; index++)
        {
            if (span[index] == (byte)'%')
            {
                while (index < span.Length && span[index] is not (byte)'\r' and not (byte)'\n') index++;
                continue;
            }
            if (span[index] == (byte)'(')
            {
                var depth = 1;
                while (++index < span.Length && depth > 0)
                {
                    if (span[index] == (byte)'\\') index++;
                    else if (span[index] == (byte)'(') depth++;
                    else if (span[index] == (byte)')') depth--;
                }
                continue;
            }
            if (span[index] == (byte)'<'
                && index + 1 < span.Length
                && span[index + 1] == (byte)'<')
            {
                index++;
                continue;
            }
            if (span[index] == (byte)'<' && (index + 1 >= span.Length || span[index + 1] != (byte)'<'))
            {
                while (++index < span.Length && span[index] != (byte)'>') { }
                continue;
            }
            if (span[index] != (byte)'/')
            {
                continue;
            }
            var start = ++index;
            while (index < span.Length && !IsPdfDelimiter(span[index])) index++;
            if (index > start)
            {
                names.Add(DecodePdfName(span[start..index]));
            }
            index--;
        }
        return names;
    }

    private static string DecodePdfName(ReadOnlySpan<byte> value)
    {
        var decoded = new byte[value.Length];
        var written = 0;
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == (byte)'#'
                && index + 2 < value.Length
                && TryHex(value[index + 1], out var high)
                && TryHex(value[index + 2], out var low))
            {
                decoded[written++] = (byte)((high << 4) | low);
                index += 2;
            }
            else
            {
                decoded[written++] = value[index];
            }
        }
        return Encoding.Latin1.GetString(decoded, 0, written);
    }

    private static bool TryHex(byte value, out int result)
    {
        result = value switch
        {
            >= (byte)'0' and <= (byte)'9' => value - (byte)'0',
            >= (byte)'a' and <= (byte)'f' => value - (byte)'a' + 10,
            >= (byte)'A' and <= (byte)'F' => value - (byte)'A' + 10,
            _ => -1
        };
        return result >= 0;
    }

    private static int CountPdfObjects(byte[] bytes)
    {
        var count = 0;
        var text = Encoding.Latin1.GetString(bytes);
        for (var index = 0; (index = text.IndexOf(" obj", index, StringComparison.Ordinal)) >= 0; index += 4)
        {
            count++;
        }
        return count;
    }

    private static bool IsPdfDelimiter(byte value) =>
        value is 0 or 9 or 10 or 12 or 13 or 32
            or (byte)'(' or (byte)')' or (byte)'<' or (byte)'>'
            or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}'
            or (byte)'/' or (byte)'%';

    private static async Task<byte[]?> ReadBoundedAsync(
        Stream content,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            var read = await content.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return memory.ToArray();
            }
            total += read;
            if (total > maximumBytes)
            {
                return null;
            }
            await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static AttachmentContentValidation Valid(string mediaType, byte[] canonical) =>
        new(true, mediaType, canonical.LongLength, SHA256.HashData(canonical), null, canonical);

    private static AttachmentContentValidation Invalid(
        long size,
        string code,
        byte[]? value = null) =>
        new(false, null, size, value is null ? [] : SHA256.HashData(value), code);

    private static string NormalizeMediaType(string value) =>
        (value ?? string.Empty).Split(';', 2)[0].Trim().ToLowerInvariant();

    private readonly record struct PdfInspection(
        bool ActiveContent,
        bool LimitExceeded,
        long DecodedBytes);
}
