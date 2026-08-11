using CoreGraphics;
using Foundation;
using IPasswrd.Mobile.Services;
using UIKit;

namespace IPasswrd.Mobile.Platforms.iOS.Services;

/// <summary>
/// Уменьшение и перекодирование картинки средствами UIKit. Заодно решается вопрос HEIC:
/// снимок с камеры iPhone приходит в нём, а в сейфе нужен формат, который откроет и Windows.
/// </summary>
public sealed class ImageShrinkIos : IImageShrink
{
    public byte[]? ToJpeg(byte[] raw, int maxSide, int quality)
    {
        using NSData src = NSData.FromArray(raw);
        using UIImage? img = UIImage.LoadFromData(src);
        if (img is null) return null;

        double w = img.Size.Width, h = img.Size.Height;
        if (w <= 0 || h <= 0) return null;

        double scale = Math.Min(1.0, maxSide / Math.Max(w, h));
        var target = new CGSize(
            Math.Max(1, Math.Round(w * scale)),
            Math.Max(1, Math.Round(h * scale)));

        // Opaque + белая заливка — прозрачный PNG иначе становится чёрным,
        // а сканы почти всегда на белом. Scale = 1: нужны пиксели, а не точки экрана.
        var format = UIGraphicsImageRendererFormat.DefaultFormat;
        format.Opaque = true;
        format.Scale = 1;

        var renderer = new UIGraphicsImageRenderer(target, format);
        var area = new CGRect(CGPoint.Empty, target);
        using UIImage scaled = renderer.CreateImage(ctx =>
        {
            UIColor.White.SetFill();
            ctx.FillRect(area);
            img.Draw(area);
        });

        using NSData? jpeg = scaled.AsJPEG(Math.Clamp(quality, 1, 100) / 100f);
        return jpeg?.ToArray();
    }
}
