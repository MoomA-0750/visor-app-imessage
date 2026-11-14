using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.Versioning;

namespace VisorImessage;

/// <summary>
/// 届いた絵（写真・連絡先の顔）を、visor の画面に送る大きさの JPEG にする（画面は送り直すたびに絵も送るので、小さくしておく）。
/// 読めない形（webp など）なら null。
/// </summary>
[SupportedOSPlatform("windows")]
static class Pictures
{
    public static string? Shrink(byte[] bytes, int maxSide)
    {
        try
        {
            using var src = Image.FromStream(new MemoryStream(bytes));
            var scale = Math.Min(1, (float)maxSide / Math.Max(src.Width, src.Height));
            var w = Math.Max(1, (int)(src.Width * scale));
            var h = Math.Max(1, (int)(src.Height * scale));
            using var dst = new Bitmap(w, h);
            using (var g = Graphics.FromImage(dst))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.Clear(Color.White);
                g.DrawImage(src, 0, 0, w, h);
            }
            var jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using var p = new EncoderParameters(1);
            p.Param[0] = new EncoderParameter(Encoder.Quality, 82L);
            using var ms = new MemoryStream();
            dst.Save(ms, jpeg, p);
            return Convert.ToBase64String(ms.ToArray());
        }
        catch (Exception)
        {
            return null;
        }
    }
}
