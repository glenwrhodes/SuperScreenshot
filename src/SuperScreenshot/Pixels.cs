using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace SuperScreenshot;

/// <summary>Grabs part of the screen into a reusable buffer of 0x00RRGGBB pixels.</summary>
internal sealed class FrameGrabber : IDisposable
{
    Native.Dib? dib;

    public int[] Grab(Rectangle area, int[]? reuse = null)
    {
        if (dib == null || dib.Width != area.Width || dib.Height != area.Height)
        {
            dib?.Dispose();
            dib = new Native.Dib(area.Width, area.Height);
        }

        IntPtr screen = Native.GetDC(IntPtr.Zero);
        try
        {
            // Plain SRCCOPY: the cursor is not included, and our overlay is excluded from capture.
            Native.BitBlt(dib.Dc, 0, 0, area.Width, area.Height, screen, area.X, area.Y, Native.SRCCOPY);
        }
        finally
        {
            Native.ReleaseDC(IntPtr.Zero, screen);
        }
        Native.GdiFlush();

        int count = area.Width * area.Height;
        int[] pixels = reuse != null && reuse.Length == count ? reuse : new int[count];
        Marshal.Copy(dib.Bits, pixels, 0, count);
        return pixels;
    }

    public void Dispose()
    {
        dib?.Dispose();
        dib = null;
    }
}

internal static class Pixels
{
    /// <summary>Copies a rectangle of a pixel buffer into a new opaque bitmap.</summary>
    public static Bitmap ToBitmap(int[] pixels, int stride, Rectangle part)
    {
        return Build(part.Width, part.Height, y =>
        {
            int start = (part.Y + y) * stride + part.X;
            return (pixels, start);
        });
    }

    /// <summary>Builds an opaque bitmap one row at a time. A null row is painted white.</summary>
    public static Bitmap Build(int width, int height, Func<int, (int[]? source, int start)> rowAt)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var line = new int[width];
            for (int y = 0; y < height; y++)
            {
                var (source, start) = rowAt(y);
                if (source == null)
                {
                    Array.Fill(line, unchecked((int)0xFFFFFFFF));
                }
                else
                {
                    for (int x = 0; x < width; x++)
                        line[x] = source[start + x] | unchecked((int)0xFF000000);
                }
                Marshal.Copy(line, 0, data.Scan0 + y * data.Stride, width);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return bitmap;
    }
}
