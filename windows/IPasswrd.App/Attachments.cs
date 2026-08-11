using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using IPasswrd.Core;

namespace IPasswrd.App;

/// <summary>
/// Turning a file the user picked into something the vault will accept.
///
/// The vault travels to the cloud as one blob on every save, so a 6-megapixel phone photo of a
/// passport cannot go in untouched. Pictures are downscaled and re-encoded here; everything else
/// is taken as-is and simply refused if it is too big — silently mangling a PDF would be worse
/// than saying no.
/// </summary>
internal static class Attachments
{
    /// <summary>Longest side after downscaling. Enough to read a passport number off the scan.</summary>
    private const int MaxSide = 1600;

    /// <summary>JPEG quality. 80 is the usual point where artefacts stop being visible on text.</summary>
    private const long JpegQuality = 80L;

    private static readonly string[] PictureExtensions =
        { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp" };

    public static bool LooksLikePicture(string fileName) =>
        PictureExtensions.Contains(Path.GetExtension(fileName).ToLowerInvariant());

    /// <summary>
    /// Build the stored form of a picked file. Throws <see cref="AttachmentTooLargeException"/>
    /// when even a re-encoded picture will not fit — the caller shows that message as-is.
    /// </summary>
    public static Attachment Prepare(string fileName, byte[] raw)
    {
        byte[] data = raw;
        string mime = MimeFor(fileName);

        if (LooksLikePicture(fileName))
        {
            try
            {
                data = Shrink(raw);
                mime = "image/jpeg";
                fileName = Path.GetFileNameWithoutExtension(fileName) + ".jpg";
            }
            catch
            {
                // Not decodable as a picture after all (a .png that is really something else).
                // Fall through with the original bytes and let the size check decide.
                data = raw;
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

    /// <summary>Downscale to <see cref="MaxSide"/> and re-encode as JPEG.</summary>
    private static byte[] Shrink(byte[] raw)
    {
        using var input = new MemoryStream(raw);
        using var src = Image.FromStream(input);

        int w = src.Width, h = src.Height;
        double scale = Math.Min(1.0, (double)MaxSide / Math.Max(w, h));
        int nw = Math.Max(1, (int)Math.Round(w * scale));
        int nh = Math.Max(1, (int)Math.Round(h * scale));

        using var bmp = new Bitmap(nw, nh, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.Clear(Color.White);   // scans are usually white; keeps transparent PNGs from going black
            g.DrawImage(src, 0, 0, nw, nh);
        }

        ImageCodecInfo? jpeg = ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.MimeType == "image/jpeg");
        using var output = new MemoryStream();
        if (jpeg is null)
        {
            bmp.Save(output, ImageFormat.Jpeg);
        }
        else
        {
            using var p = new EncoderParameters(1);
            p.Param[0] = new EncoderParameter(Encoder.Quality, JpegQuality);
            bmp.Save(output, jpeg, p);
        }
        return output.ToArray();
    }

    private static string MimeFor(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".txt" => "text/plain",
        ".doc" => "application/msword",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        _ => "application/octet-stream",
    };

    /// <summary>"248 КБ" / "1,4 МБ" — for showing next to the file name.</summary>
    public static string HumanSize(int bytes) =>
        bytes >= 1024 * 1024
            ? (bytes / 1024d / 1024d).ToString("0.#", System.Globalization.CultureInfo.GetCultureInfo("ru-RU")) + " МБ"
            : Math.Max(1, bytes / 1024) + " КБ";
}
