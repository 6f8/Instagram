using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class IconExport
{
    public static void Save(DrawingImage mark, string path)
    {
        int[] sizes = [16, 24, 32, 48, 64, 128, 256];
        var frames = new List<byte[]>();
        foreach (int size in sizes)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(13,14,17)), null,
                    new Rect(0,0,size,size), size * .2, size * .2);
                dc.DrawImage(mark, new Rect(size * .13, size * .34, size * .74, size * .29));
            }
            var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var buffer = new MemoryStream();
            encoder.Save(buffer);
            frames.Add(buffer.ToArray());
        }
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length);
        int offset = 6 + sizes.Length * 16;
        for (int i = 0; i < sizes.Length; i++)
        {
            writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
            writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
            writer.Write((byte)0); writer.Write((byte)0); writer.Write((ushort)1); writer.Write((ushort)32);
            writer.Write(frames[i].Length); writer.Write(offset); offset += frames[i].Length;
        }
        foreach (var frame in frames) writer.Write(frame);
        Console.WriteLine("Exported multi-resolution L03 icon: " + path);
    }
}
