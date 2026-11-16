using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using VelaShell.Vnc;

namespace VelaShell.Plugin.Vnc;

/// <summary>
/// Decodes Tight JPEG rectangles with the host's Skia (through Avalonia's <see cref="Bitmap"/>), so neither the plugin
/// nor the protocol library ships an image codec. Called on the session's receive thread; Skia decoding needs no UI thread.
/// </summary>
internal static class VncJpeg
{
    internal static VncDecodedImage? Decode(ReadOnlySpan<byte> jpeg)
    {
        try
        {
            using var stream = new MemoryStream(jpeg.ToArray(), writable: false);
            using var decoded = new Bitmap(stream);
            PixelSize size = decoded.PixelSize;
            if (size.Width <= 0 || size.Height <= 0)
            {
                return null;
            }

            // Convert to BGRA through a writeable bitmap: the decoder's own pixel format differs between platforms.
            using var target = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            using ILockedFramebuffer buffer = target.Lock();
            decoded.CopyPixels(buffer);
            byte[] bgra = new byte[size.Width * size.Height * 4];
            for (int row = 0; row < size.Height; row++)
            {
                System.Runtime.InteropServices.Marshal.Copy(buffer.Address + row * buffer.RowBytes, bgra, row * size.Width * 4, size.Width * 4);
            }
            return new VncDecodedImage(size.Width, size.Height, bgra);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }
}
