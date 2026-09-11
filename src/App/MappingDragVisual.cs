using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Tk75.App
{
    internal static class MappingDragFeedback
    {
        internal static readonly Color AvailableColor = Color.FromArgb(128, 190, 211);
        internal static readonly Color DropColor = Color.FromArgb(255, 198, 92);
    }

    // The caller owns this one image for the whole synchronous drag. A preview
    // window may borrow it, but must be disposed before the visual is disposed.
    internal sealed class MappingDragVisual : IDisposable
    {
        public Bitmap Image { get; private set; }
        public Point Anchor { get; private set; }
        MappingDragVisual(Bitmap image, Point anchor) { Image = image; Anchor = anchor; }

        internal static MappingDragVisual Extract(Bitmap source, Bitmap mask, Rectangle crop, Point anchor)
        {
            var image = new Bitmap(crop.Width, crop.Height, PixelFormat.Format32bppPArgb);
            BitmapData sourceData = null, maskData = null, imageData = null;
            try
            {
                try
                {
                    Rectangle area = new Rectangle(Point.Empty, crop.Size);
                    sourceData = source.LockBits(crop, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                    maskData = mask.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                    imageData = image.LockBits(area, ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
                    int rowBytes = checked(crop.Width * 4);
                    var sourceRow = new byte[rowBytes]; var maskRow = new byte[rowBytes]; var resultRow = new byte[rowBytes];
                    for (int y = 0; y < crop.Height; y++)
                    {
                        Marshal.Copy(IntPtr.Add(sourceData.Scan0, y * sourceData.Stride), sourceRow, 0, rowBytes);
                        Marshal.Copy(IntPtr.Add(maskData.Scan0, y * maskData.Stride), maskRow, 0, rowBytes);
                        for (int x = 0; x < rowBytes; x += 4)
                        {
                            int alpha = maskRow[x + 3];
                            resultRow[x] = (byte)((sourceRow[x] * alpha + 127) / 255);
                            resultRow[x + 1] = (byte)((sourceRow[x + 1] * alpha + 127) / 255);
                            resultRow[x + 2] = (byte)((sourceRow[x + 2] * alpha + 127) / 255);
                            resultRow[x + 3] = (byte)alpha;
                        }
                        Marshal.Copy(resultRow, 0, IntPtr.Add(imageData.Scan0, y * imageData.Stride), rowBytes);
                    }
                }
                finally
                {
                    if (imageData != null) image.UnlockBits(imageData);
                    if (maskData != null) mask.UnlockBits(maskData);
                    if (sourceData != null) source.UnlockBits(sourceData);
                }
                return new MappingDragVisual(image, anchor);
            }
            catch { image.Dispose(); throw; }
        }
        public void Dispose()
        {
            Bitmap image = Image; Image = null;
            if (image != null) image.Dispose();
        }
    }
}
