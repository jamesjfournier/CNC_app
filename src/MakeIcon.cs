// Builds a Windows .ico from the DBA CNC logo so the exe, window, and taskbar share it.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

static class MakeIcon
{
    static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: MakeIcon.exe source.png dest.ico");
            return 1;
        }

        int[] sizes = new int[] { 16, 24, 32, 48, 64, 128, 256 };
        using (Bitmap original = new Bitmap(args[0]))
        {
            byte[][] pngs = new byte[sizes.Length][];
            for (int i = 0; i < sizes.Length; i++)
                pngs[i] = Render(original, sizes[i]);

            using (FileStream fs = File.Create(args[1]))
            using (BinaryWriter bw = new BinaryWriter(fs))
            {
                bw.Write((ushort)0);
                bw.Write((ushort)1);
                bw.Write((ushort)sizes.Length);
                int offset = 6 + (16 * sizes.Length);
                for (int i = 0; i < sizes.Length; i++)
                {
                    byte dim = sizes[i] >= 256 ? (byte)0 : (byte)sizes[i];
                    bw.Write(dim);
                    bw.Write(dim);
                    bw.Write((byte)0);
                    bw.Write((byte)0);
                    bw.Write((ushort)1);
                    bw.Write((ushort)32);
                    bw.Write(pngs[i].Length);
                    bw.Write(offset);
                    offset += pngs[i].Length;
                }
                for (int i = 0; i < sizes.Length; i++)
                    bw.Write(pngs[i]);
            }
        }
        return 0;
    }

    static byte[] Render(Bitmap original, int size)
    {
        using (Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.DrawImage(original, new Rectangle(0, 0, size, size));
            using (MemoryStream ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
        }
    }
}
