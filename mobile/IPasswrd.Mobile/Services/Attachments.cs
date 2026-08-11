using IPasswrd.Core;

namespace IPasswrd.Mobile.Services;

/// <summary>
/// Уменьшение картинки перед тем, как класть её в сейф. Реализация нативная у каждой платформы:
/// System.Drawing на телефоне нет, а тащить ради одного ресайза целый SkiaSharp — дорого.
/// </summary>
public interface IImageShrink
{
    /// <summary>JPEG не длиннее maxSide по большей стороне; null — если байты не картинка.</summary>
    byte[]? ToJpeg(byte[] raw, int maxSide, int quality);
}

public sealed class NullImageShrink : IImageShrink
{
    public byte[]? ToJpeg(byte[] raw, int maxSide, int quality) => null;
}

/// <summary>
/// Выбранный на телефоне файл → вложение сейфа.
///
/// Ровно та же логика, что в настольном приложении: сейф целиком уезжает в облако при каждом
/// сохранении, поэтому фотография паспорта на 6 мегапикселей внутрь не поедет. Картинки
/// пережимаются, всё остальное берётся как есть и просто отклоняется, если не влезает —
/// молча испортить PDF было бы хуже, чем сказать «нет».
/// </summary>
public static class Attachments
{
    /// <summary>Большая сторона после уменьшения. Хватает, чтобы прочитать номер на скане.</summary>
    private const int MaxSide = 1600;

    /// <summary>Качество JPEG. На 80 артефакты перестают быть заметны на тексте.</summary>
    private const int JpegQuality = 80;

    private static readonly string[] PictureExtensions =
        { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".heic", ".heif" };

    public static bool LooksLikePicture(string fileName) =>
        PictureExtensions.Contains(Path.GetExtension(fileName).ToLowerInvariant());

    /// <summary>Можно ли показать вложение картинкой прямо в приложении.</summary>
    public static bool IsPicture(Attachment a) =>
        a.Mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase) || LooksLikePicture(a.Name);

    /// <summary>
    /// Готовит вложение к укладке в запись. Бросает <see cref="AttachmentTooLargeException"/>,
    /// когда даже пережатая картинка не помещается — этот текст показывается человеку как есть.
    /// </summary>
    public static Attachment Prepare(string fileName, byte[] raw)
    {
        if (string.IsNullOrWhiteSpace(fileName)) fileName = "файл";
        fileName = Path.GetFileName(fileName);

        byte[] data = raw;
        string mime = MimeFor(fileName);

        if (LooksLikePicture(fileName))
        {
            byte[]? jpeg = null;
            try { jpeg = Svc.Shrink.ToJpeg(raw, MaxSide, JpegQuality); }
            catch (Exception) { /* не картинка на самом деле — оставим исходные байты */ }

            if (jpeg is { Length: > 0 })
            {
                data = jpeg;
                mime = "image/jpeg";
                fileName = Path.GetFileNameWithoutExtension(fileName) + ".jpg";
            }
        }

        if (data.Length > Vault.MaxAttachmentBytes)
        {
            throw new AttachmentTooLargeException(
                $"«{fileName}» — {data.Length / 1024} КБ. Предел {Vault.MaxAttachmentBytes / 1024} КБ: " +
                "сейф целиком уезжает в облако при каждом сохранении.");
        }

        return new Attachment
        {
            Name = fileName,
            Mime = mime,
            Bytes = data.Length,
            Data = Convert.ToBase64String(data),
            AddedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
        };
    }

    private static string MimeFor(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".heic" or ".heif" => "image/heic",
        ".txt" => "text/plain",
        ".doc" => "application/msword",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        _ => "application/octet-stream",
    };

    /// <summary>«248 КБ» / «1,4 МБ» — рядом с именем файла.</summary>
    public static string HumanSize(int bytes) =>
        bytes >= 1024 * 1024
            ? (bytes / 1024d / 1024d).ToString("0.#", System.Globalization.CultureInfo.GetCultureInfo("ru-RU")) + " МБ"
            : Math.Max(1, bytes / 1024) + " КБ";

    /// <summary>«6 августа» из ISO-даты; пусто, если даты нет или она непонятная.</summary>
    public static string AddedOn(Attachment a)
    {
        if (!DateTime.TryParse(a.AddedAt, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out DateTime utc))
            return "";
        return utc.ToLocalTime().ToString("d MMMM yyyy", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));
    }

    /// <summary>Имя без сюрпризов для временного файла: никаких путей и разделителей.</summary>
    public static string SafeFileName(string name)
    {
        string s = Path.GetFileName(name);
        foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Length == 0 ? "файл" : s;
    }
}
