using Android.Graphics;
using IPasswrd.Mobile.Services;

namespace IPasswrd.Mobile.Platforms.Android.Services;

/// <summary>
/// Уменьшение и перекодирование картинки средствами Android — аналог ImageShrinkIos.
/// BitmapFactory сам понимает HEIC/HEIF (Android 10+), на выходе всегда JPEG,
/// который откроется и на Windows.
/// </summary>
public sealed class ImageShrinkAndroid : IImageShrink
{
    public byte[]? ToJpeg(byte[] raw, int maxSide, int quality)
    {
        try
        {
            // 1) узнаём размер, не декодируя пиксели
            var probe = new BitmapFactory.Options { InJustDecodeBounds = true };
            BitmapFactory.DecodeByteArray(raw, 0, raw.Length, probe);
            int w = probe.OutWidth, h = probe.OutHeight;
            if (w <= 0 || h <= 0) return null;

            // 2) грубое уменьшение степенью двойки — иначе большой снимок не влезет в память
            int sample = 1;
            while (Math.Max(w, h) / (sample * 2) >= maxSide) sample *= 2;

            var opts = new BitmapFactory.Options { InSampleSize = sample };
            using Bitmap? decoded = BitmapFactory.DecodeByteArray(raw, 0, raw.Length, opts);
            if (decoded is null) return null;

            // 3) точное доведение до maxSide
            Bitmap target = decoded;
            Bitmap? scaled = null;
            double scale = Math.Min(1.0, (double)maxSide / Math.Max(decoded.Width, decoded.Height));
            if (scale < 1.0)
            {
                int tw = Math.Max(1, (int)Math.Round(decoded.Width * scale));
                int th = Math.Max(1, (int)Math.Round(decoded.Height * scale));
                scaled = Bitmap.CreateScaledBitmap(decoded, tw, th, true);
                if (scaled is not null) target = scaled;
            }

            try
            {
                // 4) белая подложка: прозрачный PNG иначе станет чёрным в JPEG
                using Bitmap? flat = Bitmap.CreateBitmap(target.Width, target.Height, Bitmap.Config.Argb8888!);
                if (flat is null) return null;
                using (var canvas = new Canvas(flat))
                {
                    canvas.DrawColor(Color.White);
                    canvas.DrawBitmap(target, 0, 0, null);
                }

                using var ms = new MemoryStream();
                flat.Compress(Bitmap.CompressFormat.Jpeg!, Math.Clamp(quality, 1, 100), ms);
                return ms.ToArray();
            }
            finally
            {
                scaled?.Dispose();
            }
        }
        catch (Exception)
        {
            return null;   // не картинка (или не хватило памяти) — вызывающий возьмёт файл как есть
        }
    }
}
