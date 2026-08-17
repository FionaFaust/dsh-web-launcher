// ICO 生成器：从多尺寸 PNG 位图构建标准 .ico 文件
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

public static class IconBuilder
{
    public static void Main(string[] args)
    {
        string srcPng = args[0];
        string outIco = args[1];
        int[] sizes = { 16, 32, 48, 256 };

        List<Bitmap> bmps = new List<Bitmap>();
        using (Image src = Image.FromFile(srcPng))
        {
            foreach (int s in sizes)
            {
                Bitmap bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                    g.DrawImage(src, 0, 0, s, s);
                }
                bmps.Add(bmp);
            }
        }

        using (FileStream fs = new FileStream(outIco, FileMode.Create))
        using (BinaryWriter w = new BinaryWriter(fs))
        {
            // ICONDIR
            w.Write((short)0);   // reserved
            w.Write((short)1);   // type: icon
            w.Write((short)bmps.Count);

            // 先写目录，记录偏移
            int offset = 6 + 16 * bmps.Count;
            int[] offsets = new int[bmps.Count];
            for (int i = 0; i < bmps.Count; i++)
            {
                int s = bmps[i].Width;
                byte dim = (byte)(s >= 256 ? 0 : s);
                int dataLen = s == 256 ? PngBytes(bmps[i]).Length : BmpBytes(bmps[i]).Length;
                w.Write(dim);                    // width
                w.Write(dim);                    // height
                w.Write((byte)0);                // color count
                w.Write((byte)0);                // reserved
                w.Write((short)1);               // planes
                w.Write((short)32);              // bpp
                w.Write(dataLen);
                w.Write(offset);
                offsets[i] = offset;
                offset += dataLen;
            }

            // 图像数据
            for (int i = 0; i < bmps.Count; i++)
            {
                int s = bmps[i].Width;
                if (s == 256)
                {
                    w.Write(PngBytes(bmps[i]));
                }
                else
                {
                    w.Write(BmpBytes(bmps[i]));
                }
            }
        }

        foreach (Bitmap b in bmps) b.Dispose();
        Console.WriteLine("ICO written: " + outIco);
    }

    private static byte[] PngBytes(Bitmap bmp)
    {
        using (MemoryStream ms = new MemoryStream())
        {
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
    }

    private static byte[] BmpBytes(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        int stride = w * 4; // 32bpp, 4-byte aligned
        // BITMAPINFOHEADER (40) + 像素(自底向上) + AND mask(每行对齐4字节)
        int maskStride = ((w + 31) / 32) * 4;
        int dataLen = 40 + stride * h + maskStride * h;
        byte[] data = new byte[dataLen];

        BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

        // 复制 BGRA 像素并翻转行序（BMP 自底向上）
        int srcStride = bd.Stride;
        unsafe
        {
            byte* srcRow = (byte*)bd.Scan0;
            for (int y = 0; y < h; y++)
            {
                int srcOff = y * srcStride;
                int dstOff = 40 + (h - 1 - y) * stride;
                for (int x = 0; x < w; x++)
                {
                    data[dstOff + x * 4 + 0] = srcRow[srcOff + x * 4 + 0]; // B
                    data[dstOff + x * 4 + 1] = srcRow[srcOff + x * 4 + 1]; // G
                    data[dstOff + x * 4 + 2] = srcRow[srcOff + x * 4 + 2]; // R
                    data[dstOff + x * 4 + 3] = srcRow[srcOff + x * 4 + 3]; // A
                }
            }
        }
        bmp.UnlockBits(bd);

        // BITMAPINFOHEADER
        BitConverter.GetBytes(40).CopyTo(data, 0);            // biSize
        BitConverter.GetBytes(w).CopyTo(data, 4);             // biWidth
        BitConverter.GetBytes(h * 2).CopyTo(data, 8);         // biHeight (含 mask)
        BitConverter.GetBytes((short)1).CopyTo(data, 12);     // biPlanes
        BitConverter.GetBytes((short)32).CopyTo(data, 14);    // biBitCount
        // biCompression=0, biSizeImage=0, 其余=0

        // AND mask：全 0（不透明区域由 alpha 通道处理；传统 AND mask 全 0 表示无透明洞）
        // 已默认 0，无需处理

        return data;
    }
}
