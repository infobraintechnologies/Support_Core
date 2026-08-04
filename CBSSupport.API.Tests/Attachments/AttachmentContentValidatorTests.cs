using System.IO.Compression;
using System.Text;
using CBSSupport.Shared.Services;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Wordprocessing;
using SkiaSharp;

namespace CBSSupport.API.Tests.Attachments;

public sealed class AttachmentContentValidatorTests
{
    [Theory]
    [InlineData("document.pdf", AttachmentContentValidator.PdfMediaType)]
    [InlineData("photo.jpg", AttachmentContentValidator.JpegMediaType)]
    [InlineData("photo.jpeg", AttachmentContentValidator.JpegMediaType)]
    [InlineData("photo.png", AttachmentContentValidator.PngMediaType)]
    [InlineData("document.docx", AttachmentContentValidator.DocxMediaType)]
    [InlineData("workbook.xlsx", AttachmentContentValidator.XlsxMediaType)]
    public void IsAllowedDeclaration_ApprovedPair_IsAccepted(string name, string mediaType)
    {
        Assert.True(AttachmentContentValidator.IsAllowedDeclaration(name, mediaType, out var safeName));
        Assert.Equal(name, safeName);
    }

    [Theory]
    [InlineData("legacy.doc", "application/msword")]
    [InlineData("legacy.xls", "application/vnd.ms-excel")]
    [InlineData("macro.docm", "application/vnd.ms-word.document.macroEnabled.12")]
    [InlineData("template.dotm", "application/vnd.ms-word.template.macroEnabled.12")]
    [InlineData("macro.xlsm", "application/vnd.ms-excel.sheet.macroEnabled.12")]
    [InlineData("template.xltm", "application/vnd.ms-excel.template.macroEnabled.12")]
    [InlineData("addin.xlam", "application/vnd.ms-excel.addin.macroEnabled.12")]
    [InlineData("binary.xlsb", "application/vnd.ms-excel.sheet.binary.macroEnabled.12")]
    [InlineData("archive.zip", "application/zip")]
    [InlineData("program.exe", "application/octet-stream")]
    [InlineData("script.js", "text/javascript")]
    [InlineData("page.html", "text/html")]
    [InlineData("vector.svg", "image/svg+xml")]
    [InlineData("photo.webp", "image/webp")]
    [InlineData("notes.txt", "text/plain")]
    [InlineData("rows.csv", "text/csv")]
    public void IsAllowedDeclaration_UnsupportedType_IsRejected(string name, string mediaType) =>
        Assert.False(AttachmentContentValidator.IsAllowedDeclaration(name, mediaType, out _));

    [Theory]
    [MemberData(nameof(ValidFiles))]
    public async Task ValidateAsync_ValidAllowedFile_IsStructurallyAccepted(
        string name,
        string mediaType,
        byte[] bytes)
    {
        var result = await ValidateAsync(name, mediaType, bytes);

        Assert.True(result.Valid);
        Assert.Equal(mediaType, result.DetectedMediaType);
        Assert.Equal(32, result.Sha256.Length);
        Assert.NotNull(result.CanonicalContent);
    }

    [Theory]
    [InlineData("photo.jpg", AttachmentContentValidator.PngMediaType)]
    [InlineData("document.pdf", "application/octet-stream")]
    [InlineData("workbook.xlsx", AttachmentContentValidator.DocxMediaType)]
    public async Task ValidateAsync_SpoofedDeclaredMediaType_IsRejected(string name, string mediaType)
    {
        var result = await ValidateAsync(name, mediaType, CreateValidPdf());

        Assert.False(result.Valid);
        Assert.Equal("content_type_mismatch", result.RejectionCode);
    }

    [Theory]
    [InlineData("photo.jpg", AttachmentContentValidator.JpegMediaType)]
    [InlineData("photo.png", AttachmentContentValidator.PngMediaType)]
    [InlineData("document.docx", AttachmentContentValidator.DocxMediaType)]
    [InlineData("workbook.xlsx", AttachmentContentValidator.XlsxMediaType)]
    public async Task ValidateAsync_ExtensionAndSignatureMismatch_IsRejected(string name, string mediaType)
    {
        var result = await ValidateAsync(name, mediaType, CreateValidPdf());

        Assert.False(result.Valid);
        Assert.Contains(result.RejectionCode, new[] { "content_type_mismatch", "malformed_content" });
    }

    [Theory]
    [InlineData("photo.jpg", AttachmentContentValidator.JpegMediaType, "FFD8FF")]
    [InlineData("photo.png", AttachmentContentValidator.PngMediaType, "89504E470D0A1A0A")]
    [InlineData("document.pdf", AttachmentContentValidator.PdfMediaType, "255044462D312E37")]
    [InlineData("document.docx", AttachmentContentValidator.DocxMediaType, "504B0304")]
    [InlineData("workbook.xlsx", AttachmentContentValidator.XlsxMediaType, "504B0304")]
    public async Task ValidateAsync_CorruptFile_IsRejected(string name, string mediaType, string hex)
    {
        var result = await ValidateAsync(name, mediaType, Convert.FromHexString(hex));

        Assert.False(result.Valid);
        Assert.Contains(result.RejectionCode, new[] { "malformed_content", "content_type_mismatch" });
    }

    [Fact]
    public async Task ValidateAsync_ImageOverPixelLimit_IsRejected()
    {
        var limits = new AttachmentStructuralValidationOptions { MaximumImagePixels = 3 };
        var result = await ValidateAsync(
            "photo.png",
            AttachmentContentValidator.PngMediaType,
            CreateImage(SKEncodedImageFormat.Png),
            limits);

        Assert.False(result.Valid);
        Assert.Equal("image_limit_exceeded", result.RejectionCode);
    }

    [Fact]
    public async Task ValidateAsync_OfficePackageOverEntryLimit_IsRejected()
    {
        var limits = new AttachmentStructuralValidationOptions { MaximumPackageEntries = 1 };
        var result = await ValidateAsync(
            "document.docx",
            AttachmentContentValidator.DocxMediaType,
            CreateDocx(),
            limits);

        Assert.False(result.Valid);
        Assert.Equal("package_limit_exceeded", result.RejectionCode);
    }

    [Fact]
    public async Task ValidateAsync_HighCompressionRatioOfficeEntry_IsRejected()
    {
        var bytes = AddZipEntry(CreateDocx(), "word/media/padding.bin", new byte[32 * 1024]);
        var limits = new AttachmentStructuralValidationOptions { MaximumPackageCompressionRatio = 2 };
        var result = await ValidateAsync(
            "document.docx",
            AttachmentContentValidator.DocxMediaType,
            bytes,
            limits);

        Assert.False(result.Valid);
        Assert.Equal("package_limit_exceeded", result.RejectionCode);
    }

    [Theory]
    [InlineData(true, "word/vbaProject.bin")]
    [InlineData(false, "xl/vbaProject.bin")]
    [InlineData(true, "word/embeddings/oleObject1.bin")]
    [InlineData(false, "xl/oleObjects/oleObject1.bin")]
    [InlineData(true, "word/embeddings/payload.exe")]
    [InlineData(false, "xl/embeddings/payload.exe")]
    public async Task ValidateAsync_OfficeActivePart_IsRejected(bool word, string entryName)
    {
        var bytes = AddZipEntry(word ? CreateDocx() : CreateXlsx(), entryName, [1, 2, 3]);
        var result = await ValidateAsync(
            word ? "document.docx" : "workbook.xlsx",
            word ? AttachmentContentValidator.DocxMediaType : AttachmentContentValidator.XlsxMediaType,
            bytes);

        Assert.False(result.Valid);
        Assert.Equal("active_content", result.RejectionCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ValidateAsync_OfficeExecutableDisguisedAsBin_IsRejected(bool word)
    {
        var bytes = AddZipEntry(
            word ? CreateDocx() : CreateXlsx(),
            word ? "word/media/payload.bin" : "xl/media/payload.bin",
            [(byte)'M', (byte)'Z', 0, 0, 0, 0, 0, 0]);
        var result = await ValidateAsync(
            word ? "document.docx" : "workbook.xlsx",
            word ? AttachmentContentValidator.DocxMediaType : AttachmentContentValidator.XlsxMediaType,
            bytes);

        Assert.False(result.Valid);
        Assert.Equal("active_content", result.RejectionCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ValidateAsync_OfficeExternalRelationship_IsRejected(bool word)
    {
        const string relationship =
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rIdExternal\" Type=\"urn:test\" Target=\"https://example.invalid/data\" TargetMode=\"External\"/>" +
            "</Relationships>";
        var bytes = AddZipEntry(
            word ? CreateDocx() : CreateXlsx(),
            word ? "word/_rels/unsafe.rels" : "xl/_rels/unsafe.rels",
            Encoding.UTF8.GetBytes(relationship));
        var result = await ValidateAsync(
            word ? "document.docx" : "workbook.xlsx",
            word ? AttachmentContentValidator.DocxMediaType : AttachmentContentValidator.XlsxMediaType,
            bytes);

        Assert.False(result.Valid);
        Assert.Equal("active_content", result.RejectionCode);
    }

    [Theory]
    [InlineData("document.docx", AttachmentContentValidator.DocxMediaType)]
    [InlineData("workbook.xlsx", AttachmentContentValidator.XlsxMediaType)]
    public async Task ValidateAsync_EncryptedOfficeContainer_IsRejected(string name, string mediaType)
    {
        byte[] compoundFileSignature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
        var result = await ValidateAsync(name, mediaType, compoundFileSignature);

        Assert.False(result.Valid);
        Assert.Equal("encrypted_content", result.RejectionCode);
    }

    [Theory]
    [InlineData("/Encrypt")]
    [InlineData("/JavaScript")]
    [InlineData("/EmbeddedFile")]
    [InlineData("/Launch")]
    [InlineData("/RichMedia")]
    public async Task ValidateAsync_PdfEncryptedOrActiveContent_IsRejected(string forbiddenName)
    {
        var bytes = CreateValidPdf(forbiddenName);
        var result = await ValidateAsync("document.pdf", AttachmentContentValidator.PdfMediaType, bytes);

        Assert.False(result.Valid);
        Assert.Equal(
            forbiddenName == "/Encrypt" ? "encrypted_content" : "active_content",
            result.RejectionCode);
    }

    [Fact]
    public async Task ValidateAsync_PdfOverObjectLimit_IsRejected()
    {
        var limits = new AttachmentStructuralValidationOptions { MaximumPdfObjects = 1 };
        var result = await ValidateAsync(
            "document.pdf",
            AttachmentContentValidator.PdfMediaType,
            CreateValidPdf(),
            limits);

        Assert.False(result.Valid);
        Assert.Equal("pdf_limit_exceeded", result.RejectionCode);
    }

    public static TheoryData<string, string, byte[]> ValidFiles => new()
    {
        { "photo.jpg", AttachmentContentValidator.JpegMediaType, CreateImage(SKEncodedImageFormat.Jpeg) },
        { "photo.png", AttachmentContentValidator.PngMediaType, CreateImage(SKEncodedImageFormat.Png) },
        { "document.pdf", AttachmentContentValidator.PdfMediaType, CreateValidPdf() },
        { "document.docx", AttachmentContentValidator.DocxMediaType, CreateDocx() },
        { "workbook.xlsx", AttachmentContentValidator.XlsxMediaType, CreateXlsx() }
    };

    private static async Task<AttachmentContentValidation> ValidateAsync(
        string name,
        string mediaType,
        byte[] bytes,
        AttachmentStructuralValidationOptions? limits = null)
    {
        await using var stream = new MemoryStream(bytes, writable: false);
        return await AttachmentContentValidator.ValidateAsync(
            stream,
            name,
            mediaType,
            10 * 1024 * 1024,
            limits: limits);
    }

    private static byte[] CreateImage(SKEncodedImageFormat format)
    {
        using var bitmap = new SKBitmap(2, 2, SKColorType.Rgba8888, SKAlphaType.Premul);
        bitmap.Erase(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90);
        return data.ToArray();
    }

    private static byte[] CreateDocx()
    {
        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, true))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new Document(new Body(new Paragraph(
                new DocumentFormat.OpenXml.Wordprocessing.Run(
                    new DocumentFormat.OpenXml.Wordprocessing.Text("safe")))));
            main.Document.Save();
        }
        return stream.ToArray();
    }

    private static byte[] CreateXlsx()
    {
        using var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook, true))
        {
            var workbook = document.AddWorkbookPart();
            workbook.Workbook = new Workbook();
            var worksheet = workbook.AddNewPart<WorksheetPart>();
            worksheet.Worksheet = new Worksheet(new SheetData());
            workbook.Workbook.AppendChild(new Sheets(
                new Sheet { Id = workbook.GetIdOfPart(worksheet), SheetId = 1, Name = "Sheet1" }));
            workbook.Workbook.Save();
        }
        return stream.ToArray();
    }

    private static byte[] AddZipEntry(byte[] package, string name, byte[] value)
    {
        using var stream = new MemoryStream();
        stream.Write(package);
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true))
        {
            var entry = archive.CreateEntry(name, CompressionLevel.SmallestSize);
            using var entryStream = entry.Open();
            entryStream.Write(value);
        }
        return stream.ToArray();
    }

    private static byte[] CreateValidPdf(string? extraCatalogName = null)
    {
        var objects = new[]
        {
            $"<< /Type /Catalog /Pages 2 0 R {extraCatalogName ?? string.Empty} >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Resources << >> >>",
            "<< /Length 0 >>\nstream\n\nendstream"
        };
        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true) { NewLine = "\n" };
        writer.WriteLine("%PDF-1.4");
        writer.Flush();
        var offsets = new List<long>();
        for (var index = 0; index < objects.Length; index++)
        {
            offsets.Add(stream.Position);
            writer.WriteLine($"{index + 1} 0 obj");
            writer.WriteLine(objects[index]);
            writer.WriteLine("endobj");
            writer.Flush();
        }
        var xref = stream.Position;
        writer.WriteLine("xref");
        writer.WriteLine($"0 {objects.Length + 1}");
        writer.WriteLine("0000000000 65535 f ");
        foreach (var offset in offsets)
        {
            writer.WriteLine($"{offset:0000000000} 00000 n ");
        }
        writer.WriteLine($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>");
        writer.WriteLine($"startxref\n{xref}\n%%EOF");
        writer.Flush();
        return stream.ToArray();
    }
}
