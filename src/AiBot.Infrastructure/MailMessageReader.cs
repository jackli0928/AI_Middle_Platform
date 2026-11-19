using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using AiBot.Application;
using AiBot.Domain;
using ExcelDataReader;
using MimeKit;
using System.Net;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace AiBot.Infrastructure;

internal static class MailMessageReader
{
    private const int PreviewLimit = 24_000;
    private const int MaxAttachments = 64;

    static MailMessageReader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static MimeMessage Read(byte[] raw)
    {
        if (raw.Length is 0 or > MailSorting.MaxMailBytes)
            throw new InvalidMailException("请选择 12 MB 以内的 .eml 邮件。");
        try
        {
            using var stream = new MemoryStream(raw, writable: false);
            var mail = MimeMessage.Load(stream);
            if (mail.From.Count == 0 && string.IsNullOrWhiteSpace(mail.Subject))
                throw new InvalidMailException("邮件缺少发件人和主题。");
            return mail;
        }
        catch (FormatException)
        {
            throw new InvalidMailException("邮件格式无效。");
        }
    }

    public static string Body(MimeMessage mail)
    {
        var text = mail.TextBody;
        if (string.IsNullOrWhiteSpace(text) && mail.HtmlBody is { } html)
            text = HtmlText(Trim(html, 1_000_000));
        return Trim(text, 120_000);
    }

    public static IReadOnlyList<MailAttachment> Previews(MimeMessage mail) => mail.Attachments
        .Take(MaxAttachments).Select(Preview).ToArray();

    public static (byte[] Data, string Name)? Download(MimeMessage mail, int index)
    {
        if (index is < 0 or >= MaxAttachments || mail.Attachments.ElementAtOrDefault(index) is not { } part)
            return null;
        return (Bytes(part), Name(part));
    }

    private static MailAttachment Preview(MimeEntity part)
    {
        var name = Name(part);
        try
        {
            var extension = Path.GetExtension(name).ToLowerInvariant();
            if (extension is not (".txt" or ".csv" or ".tsv" or ".xls" or ".xlsx" or ".docx" or ".pdf"))
                return new(name, "", "请下载原件人工核对。");
            var data = Bytes(part);
            if (data.Length > (extension == ".pdf" ? 3 : 8) * 1024 * 1024)
                return new(name, "", "附件超过文本预览处理上限。");
            using var stream = new MemoryStream(data, writable: false);
            var text = extension switch
            {
                ".xls" or ".xlsx" => Excel(stream, extension),
                ".docx" => Word(stream),
                ".pdf" => Pdf(stream),
                _ => ReadText(data)
            };
            text = Trim(text, PreviewLimit);
            return new(name, text, string.IsNullOrWhiteSpace(text) ? "未提取到文字，请核对原件。" : null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new(name, "", "无法提取文字，请下载原件人工核对。");
        }
    }

    private static byte[] Bytes(MimeEntity part)
    {
        using var stream = new MemoryStream();
        if (part is MimePart leaf) leaf.Content?.DecodeTo(stream);
        if (part is MessagePart message) message.Message?.WriteTo(stream);
        return stream.ToArray();
    }

    private static string Name(MimeEntity part)
    {
        var name = part.ContentDisposition?.FileName ?? part.ContentType.Name;
        return Trim(Path.GetFileName((name ?? (part is MessagePart ? "邮件.eml" : "附件"))
            .Replace('\\', '/')), 200);
    }

    private static string Excel(Stream stream, string extension)
    {
        if (extension == ".xlsx")
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            if (archive.Entries.Count > 500 || archive.Entries.Sum(x => x.Length) > 32 * 1024 * 1024)
                return "";
        }
        stream.Position = 0;
        using var reader = ExcelReaderFactory.CreateReader(stream);
        var result = new StringBuilder();
        var sheets = 0;
        do
        {
            result.AppendLine(reader.Name);
            for (var row = 0; row < 300 && result.Length < PreviewLimit && reader.Read(); row++)
            {
                var values = Enumerable.Range(0, Math.Min(reader.FieldCount, 60))
                    .Select(i => Trim(Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture), 400));
                result.AppendLine(string.Join('\t', values));
            }
        } while (++sheets < 10 && result.Length < PreviewLimit && reader.NextResult());
        return result.ToString();
    }

    private static string Word(Stream stream)
    {
        using var archive = new ZipArchive(stream);
        var entry = archive.GetEntry("word/document.xml");
        if (entry is null || entry.Length > 4 * 1024 * 1024) return "";
        using var document = entry.Open();
        using var xml = XmlReader.Create(document, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024
        });
        var result = new StringBuilder();
        while (!xml.EOF && result.Length < PreviewLimit)
        {
            if (xml.NodeType == XmlNodeType.Element && xml.LocalName == "t")
            {
                result.Append(xml.ReadElementContentAsString());
                continue;
            }
            if (xml.NodeType == XmlNodeType.EndElement && xml.LocalName is "p" or "tc")
                result.Append(xml.LocalName == "p" ? '\n' : '\t');
            xml.Read();
        }
        return result.ToString();
    }

    private static string Pdf(Stream stream)
    {
        using var pdf = PdfDocument.Open(stream);
        var result = new StringBuilder();
        for (var page = 1; page <= Math.Min(pdf.NumberOfPages, 10) && result.Length < PreviewLimit; page++)
            result.AppendLine(ContentOrderTextExtractor.GetText(pdf.GetPage(page)));
        return result.ToString();
    }

    private static string HtmlText(string html)
    {
        const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.NonBacktracking;
        var text = Regex.Replace(html, @"(?s)<(?:script|style|head)\b[^>]*>.*?</(?:script|style|head)\s*>", "", options);
        text = Regex.Replace(text, @"<\s*(?:br\b|/(?:p|div|li|tr|h[1-6])\b)[^>]*>", "\n", options);
        text = Regex.Replace(text, @"</(?:td|th)\b[^>]*>", "\t", options);
        return WebUtility.HtmlDecode(Regex.Replace(text, @"<[^>]*>", "", options));
    }

    private static string ReadText(byte[] data)
    {
        using var stream = new MemoryStream(data, writable: false);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        var buffer = new char[PreviewLimit];
        try
        {
            return new string(buffer, 0, reader.ReadBlock(buffer));
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding("GB18030").GetString(data, 0, Math.Min(data.Length, PreviewLimit * 4));
        }
    }

    internal static string Trim(string? value, int limit)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value[..Math.Min(value.Length, limit)].Replace("\0", "").Trim();
    }
}
