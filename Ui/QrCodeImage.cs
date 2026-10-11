using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using GKME.Core;

namespace GKME.Ui;

/// <summary>Renders a <see cref="QrCode"/> module grid into an Avalonia bitmap.</summary>
internal static class QrCodeImage
{
    public static Bitmap Render(string text, QrEcc ecc = QrEcc.Medium, int scale = 4, int border = 4)
    {
        var qr = QrCode.EncodeText(text, ecc);
        int size = qr.Size;
        int dim = (size + border * 2) * scale;

        var bmp = new WriteableBitmap(
            new PixelSize(dim, dim),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);

        using (var fb = bmp.Lock())
        {
            unsafe
            {
                var ptr = (byte*)fb.Address;
                int stride = fb.RowBytes;

                for (int py = 0; py < dim; py++)
                {
                    int my = py / scale - border;
                    for (int px = 0; px < dim; px++)
                    {
                        int mx = px / scale - border;
                        bool dark = mx >= 0 && my >= 0 && mx < size && my < size && qr.GetModule(mx, my);
                        byte v = dark ? (byte)0 : (byte)255;
                        int o = py * stride + px * 4;
                        ptr[o + 0] = v;
                        ptr[o + 1] = v;
                        ptr[o + 2] = v;
                        ptr[o + 3] = 255;
                    }
                }
            }
        }

        return bmp;
    }
}
