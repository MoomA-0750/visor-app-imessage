using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace VisorImessage;

/// <summary>
/// 届いた絵（写真・連絡先の顔）を、visor の画面に送る大きさの JPEG にする（画面は送り直すたびに絵も送るので、小さくしておく）。
/// 読めない形（webp など）なら null。
/// </summary>
[SupportedOSPlatform("windows")]
static class Pictures
{
    static readonly Color[] Palette =
    [
        Color.FromArgb(0x5B, 0x8D, 0xEF), Color.FromArgb(0x3F, 0xA7, 0x7A), Color.FromArgb(0xE0, 0x8A, 0x3C), Color.FromArgb(0xC2, 0x5B, 0x9C),
        Color.FromArgb(0x8A, 0x6C, 0xD8), Color.FromArgb(0x4A, 0xA8, 0xC0), Color.FromArgb(0xD0, 0x5C, 0x5C), Color.FromArgb(0x7D, 0x8B, 0x99),
    ];

    /// <summary>
    /// 顔の絵の無い人の丸（名前の頭文字か、電話番号なら人の形）。正方形の PNG で返す
    /// （visor の部品で丸を組むと、字の高さで縦長になった）。色は名前で決まる。
    /// </summary>
    public static string Initial(string letter, string seed, int size)
    {
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            var hash = 0;
            foreach (var c in seed) hash = hash * 31 + c;
            using var fill = new SolidBrush(Palette[(hash & 0x7fffffff) % Palette.Length]);
            g.FillEllipse(fill, 0, 0, size - 1, size - 1);
            using var white = new SolidBrush(Color.White);
            if (letter.Length == 0)
            {
                // 人の形（頭と肩）
                var u = size / 100f;
                g.FillEllipse(white, 36 * u, 20 * u, 28 * u, 28 * u);
                g.FillPie(white, 22 * u, 54 * u, 56 * u, 56 * u, 180, 180);
            }
            else
            {
                using var font = new Font("Yu Gothic UI", size * 0.42f, FontStyle.Bold, GraphicsUnit.Pixel);
                using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(letter, font, white, new RectangleF(0, size * 0.02f, size, size), fmt);
            }
        }
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return Convert.ToBase64String(ms.ToArray());
    }

    /// <summary>
    /// この PC のファイルの小さな絵を、Windows のエクスプローラーと同じ縮小版から取る（iCloud 写真のようにクラウドにしか無いファイルも、
    /// 全部を落とさずに出せる）。取れなければ、手元にあるファイルだけ中身から作る。
    /// </summary>
    public static string? FileThumb(string path, int side)
    {
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            SHCreateItemFromParsingName(path, IntPtr.Zero, iid, out var factory);
            try
            {
                if (factory.GetImage(new NativeSize { cx = side, cy = side }, SIIGBF_BIGGERSIZEOK | SIIGBF_THUMBNAILONLY, out var hbm) == 0 && hbm != IntPtr.Zero)
                {
                    try
                    {
                        using var bmp = Image.FromHbitmap(hbm);
                        using var ms = new MemoryStream();
                        bmp.Save(ms, ImageFormat.Png);
                        return Shrink(ms.ToArray(), side);
                    }
                    finally
                    {
                        DeleteObject(hbm);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(factory);
            }
        }
        catch (Exception)
        {
            // 下で中身から作る
        }
        // クラウドにしか無いもの（Offline・RecallOnDataAccess）は、読むと落としてしまうので作らない
        var attrs = (int)File.GetAttributes(path);
        if ((attrs & (0x1000 | 0x400000 | 0x40000)) != 0) return null;
        return Shrink(File.ReadAllBytes(path), side);
    }

    // 縮小版だけ（無ければ、ファイルの種類のアイコンを返さずに失敗させる）
    const int SIIGBF_BIGGERSIZEOK = 0x1, SIIGBF_THUMBNAILONLY = 0x8;

    [StructLayout(LayoutKind.Sequential)]
    struct NativeSize
    {
        public int cx, cy;
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, int flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItemImageFactory ppv);

    [DllImport("gdi32.dll")]
    static extern bool DeleteObject(IntPtr hObject);

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
