using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Text;

namespace MitViewer;

/// <summary>
/// mint title format "HoshihanaKyoko-III" (.mit) のデコーダ。
/// 仕様: MIT.5.txt rev1.1 (1993/09/18 by Chaola)
/// </summary>
public sealed class MitImage
{
    /// <summary>X68000 の画面サイズ。画像はこの範囲からはみ出せない。</summary>
    public const int ScreenWidth = 768;
    public const int ScreenHeight = 512;

    /// <summary>表示位置 X は 8 ドット単位に丸められる。</summary>
    public const int OriginXAlignment = 8;

    public string Comment { get; private set; } = "";
    public int Depth { get; private set; }
    public int OriginX { get; private set; }
    public int OriginY { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public ushort[] Palette { get; private set; } = Array.Empty<ushort>();
    public byte[] PixelIndices { get; private set; } = Array.Empty<byte>();

    private byte[] _raw = Array.Empty<byte>();
    private int _originOffset;

    private MitImage() { }

    public static MitImage Load(string path) => Parse(File.ReadAllBytes(path));

    public static MitImage Parse(byte[] data)
    {
        var img = new MitImage();
        var r = new Reader(data);

        if (r.ReadU32() != 0x484B3033u) // 'HK03'
            throw new InvalidDataException("マジックナンバ 'HK03' がありません。.mit ファイルではありません。");

        int commentStart = r.Position;
        while (r.PeekByte() != 0x1a) r.ReadByte();
        int commentLen = r.Position - commentStart;
        r.ReadByte(); // EOF(0x1a)
        img.Comment = ShiftJis.GetString(data, commentStart, commentLen).TrimEnd('\r', '\n');

        img.Depth = r.ReadByte();
        if (img.Depth < 1 || img.Depth > 8)
            throw new InvalidDataException($"未対応の depth です: {img.Depth}");

        img._raw = data;
        img._originOffset = r.Position;
        img.OriginX = r.ReadU16();
        img.OriginY = r.ReadU16();
        img.Width = r.ReadU16();
        img.Height = r.ReadU16();
        if (img.Width <= 0 || img.Height <= 0)
            throw new InvalidDataException($"不正な画像サイズです: {img.Width}x{img.Height}");

        int paletteCount = 1 << img.Depth;
        img.Palette = new ushort[paletteCount];
        for (int i = 0; i < paletteCount; i++) img.Palette[i] = r.ReadU16();

        byte[] planes = DecodePlanes(r, img.Depth, img.Width, img.Height);
        img.PixelIndices = ExpandPlanes(planes, img.Depth, img.Width, img.Height);
        img.SetOrigin(img.OriginX, img.OriginY);
        return img;
    }

    /// <summary>表示位置を 768x512 の画面内に収まるよう補正して設定する。</summary>
    public void SetOrigin(int x, int y)
    {
        x -= x % OriginXAlignment;
        OriginX = Math.Clamp(x, 0, Math.Max(0, ScreenWidth - Width));
        OriginY = Math.Clamp(y, 0, Math.Max(0, ScreenHeight - Height));
    }

    /// <summary>画像を画面中央に配置する。</summary>
    public void CenterOrigin() => SetOrigin((ScreenWidth - Width) / 2, (ScreenHeight - Height) / 2);

    /// <summary>元データのヘッダの表示位置だけを書き換えて保存する。</summary>
    public void Save(string path)
    {
        var output = (byte[])_raw.Clone();
        output[_originOffset] = (byte)(OriginX >> 8);
        output[_originOffset + 1] = (byte)OriginX;
        output[_originOffset + 2] = (byte)(OriginY >> 8);
        output[_originOffset + 3] = (byte)OriginY;
        File.WriteAllBytes(path, output);
    }

    /// <summary>パレットを差し替える（色数は変更不可）。</summary>
    public void SetPalette(ushort[] newPalette)
    {
        if (newPalette.Length != Palette.Length)
            throw new ArgumentException($"パレット数が一致しません（現在 {Palette.Length} 色、指定 {newPalette.Length} 色）。");

        Array.Copy(newPalette, Palette, newPalette.Length);
        int paletteOffset = _originOffset + 8; // x_org,y_org,x_size,y_size (各word) の後ろ
        for (int i = 0; i < Palette.Length; i++)
        {
            _raw[paletteOffset + i * 2] = (byte)(Palette[i] >> 8);
            _raw[paletteOffset + i * 2 + 1] = (byte)Palette[i];
        }
    }

    /// <summary>
    /// パレット4色の BMP を取り込んで .mit 形式の画像に変換する。
    /// 4色のうち黒、または黒に最も近い色を背景色（パレット0）として扱う。
    /// </summary>
    public static MitImage FromBitmap(Bitmap source, string comment = "")
    {
        int width = source.Width;
        int height = source.Height;
        if (width <= 0 || height <= 0 || width > ScreenWidth || height > ScreenHeight)
            throw new InvalidDataException($"画像サイズが不正です（{width}x{height}）。768x512 以内にしてください。");

        // ピクセルを 32bpp で読み出し、使用されている色を収集する。
        using Bitmap argb = ToArgb32(source);
        var colorIndex = new Dictionary<int, int>();
        var colors = new List<int>();
        var pixels = new int[width * height];

        Rectangle rect = new(0, 0, width, height);
        BitmapData bits = argb.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var line = new int[width];
            for (int y = 0; y < height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(bits.Scan0 + y * bits.Stride, line, 0, width);
                int outRow = y * width;
                for (int x = 0; x < width; x++)
                {
                    int argbValue = line[x] | unchecked((int)0xFF000000); // アルファは無視
                    if (!colorIndex.TryGetValue(argbValue, out int idx))
                    {
                        if (colors.Count >= 4)
                            throw new InvalidDataException("画像に含まれる色数が4色を超えています。パレット4色の BMP を指定してください。");
                        idx = colors.Count;
                        colors.Add(argbValue);
                        colorIndex[argbValue] = idx;
                    }
                    pixels[outRow + x] = idx;
                }
            }
        }
        finally
        {
            argb.UnlockBits(bits);
        }

        while (colors.Count < 4) colors.Add(Color.Black.ToArgb()); // 使用色が4色未満なら黒で埋める

        // 黒、または黒に最も近い色をパレット0（背景色）にする。
        int bgSlot = 0;
        long bestDistance = long.MaxValue;
        for (int i = 0; i < colors.Count; i++)
        {
            Color c = Color.FromArgb(colors[i]);
            long distance = (long)c.R * c.R + (long)c.G * c.G + (long)c.B * c.B;
            if (distance < bestDistance) { bestDistance = distance; bgSlot = i; }
        }

        if (bgSlot != 0)
        {
            (colors[0], colors[bgSlot]) = (colors[bgSlot], colors[0]);
            for (int i = 0; i < pixels.Length; i++)
            {
                if (pixels[i] == 0) pixels[i] = bgSlot;
                else if (pixels[i] == bgSlot) pixels[i] = 0;
            }
        }

        var palette = new ushort[4];
        for (int i = 0; i < 4; i++) palette[i] = ToPaletteValue(Color.FromArgb(colors[i]));

        var img = new MitImage
        {
            Comment = comment,
            Depth = 2,
            Width = width,
            Height = height,
            Palette = palette,
            PixelIndices = pixels.Select(p => (byte)p).ToArray(),
        };
        img.CenterOrigin();
        img._raw = Encode(img);
        img._originOffset = 4 + ShiftJis.GetByteCount(comment) + 1 + 1; // magic + comment + EOF + depth
        return img;
    }

    private static Bitmap ToArgb32(Bitmap source)
    {
        if (source.PixelFormat == PixelFormat.Format32bppArgb) return (Bitmap)source.Clone();
        var copy = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(copy)) g.DrawImageUnscaled(source, 0, 0);
        return copy;
    }

    /// <summary>X68000 パレット値 (GGGGGRRRRRBBBBBI) への変換。<see cref="ToColor"/> の逆変換。</summary>
    public static ushort ToPaletteValue(Color c)
    {
        (int v5At0, int err0) = BestChannelFit(c.R, 0);
        (int rv5At1, int rerr1) = BestChannelFit(c.R, 1);
        (int gv5At0, int gerr0) = BestChannelFit(c.G, 0);
        (int gv5At1, int gerr1) = BestChannelFit(c.G, 1);
        (int bv5At0, int berr0) = BestChannelFit(c.B, 0);
        (int bv5At1, int berr1) = BestChannelFit(c.B, 1);

        int cost0 = err0 + gerr0 + berr0;
        int cost1 = rerr1 + gerr1 + berr1;
        int i = cost1 < cost0 ? 1 : 0;
        int r5 = i == 0 ? v5At0 : rv5At1;
        int g5 = i == 0 ? gv5At0 : gv5At1;
        int b5 = i == 0 ? bv5At0 : bv5At1;

        return (ushort)((g5 << 11) | (r5 << 6) | (b5 << 1) | i);
    }

    private static (int v5, int error) BestChannelFit(int component, int i)
    {
        int bestV5 = 0, bestErr = int.MaxValue;
        for (int v5 = 0; v5 <= 0x1f; v5++)
        {
            int err = Math.Abs(Scale(v5, i) - component);
            if (err < bestErr) { bestErr = err; bestV5 = v5; }
        }
        return (bestV5, bestErr);
    }

    /// <summary>圧縮なしのラスタ形式で .mit バイナリを組み立てる。</summary>
    private static byte[] Encode(MitImage img)
    {
        using var ms = new MemoryStream();
        void WriteU16(int v) { ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }

        ms.WriteByte(0x48); ms.WriteByte(0x4B); ms.WriteByte(0x30); ms.WriteByte(0x33); // 'HK03'
        byte[] commentBytes = ShiftJis.GetBytes(img.Comment);
        ms.Write(commentBytes, 0, commentBytes.Length);
        ms.WriteByte(0x1a);
        ms.WriteByte((byte)img.Depth);
        WriteU16(img.OriginX);
        WriteU16(img.OriginY);
        WriteU16(img.Width);
        WriteU16(img.Height);
        foreach (ushort p in img.Palette) WriteU16(p);

        byte[] planes = CollapsePixels(img.PixelIndices, img.Depth, img.Width, img.Height);
        int units = (img.Width + 7) / 8;
        int rowBytes = units * img.Depth;
        for (int y = 0; y < img.Height; y++)
        {
            ms.WriteByte(0x00); // 圧縮なし（ベタ格納）
            ms.Write(planes, y * rowBytes, rowBytes);
        }
        return ms.ToArray();
    }

    /// <summary>ピクセル毎のパレット番号をプレーン形式に変換する。<see cref="ExpandPlanes"/> の逆変換。</summary>
    private static byte[] CollapsePixels(byte[] pixels, int depth, int width, int height)
    {
        int units = (width + 7) / 8;
        int rowBytes = units * depth;
        var planes = new byte[rowBytes * height];

        for (int y = 0; y < height; y++)
        {
            int row = y * rowBytes;
            int srcRow = y * width;
            for (int u = 0; u < units; u++)
            {
                int unitOffset = row + u * depth;
                int x0 = u * 8;
                for (int b = 0; b < 8; b++)
                {
                    int x = x0 + b;
                    if (x >= width) break;
                    int shift = 7 - b;
                    int index = pixels[srcRow + x];
                    for (int p = 0; p < depth; p++)
                        planes[unitOffset + p] |= (byte)(((index >> p) & 1) << shift);
                }
            }
        }
        return planes;
    }

    /// <summary>ラスタ単位の圧縮を展開し、プレーン形式のビットマップを返す。</summary>
    private static byte[] DecodePlanes(Reader r, int depth, int width, int height)
    {
        int units = (width + 7) / 8;            // 横 8 ドット = 1 画素
        int bytesPerUnit = depth;               // 1 画素 = プレーン数バイト
        int rowBytes = units * bytesPerUnit;
        int flagBytes = (width - 1) / 32 + 1;   // コピーフラグビット列
        var planes = new byte[rowBytes * height];

        for (int y = 0; y < height; y++)
        {
            int row = y * rowBytes;
            byte header = r.ReadByte();

            if ((header & 0x80) == 0)
            {
                // 圧縮なし: 1 ラスタ分ベタ
                r.ReadBytes(planes, row, rowBytes);
                continue;
            }

            if ((header & 0x40) != 0)
            {
                // 上ラスタを全コピー
                if (y > 0) Buffer.BlockCopy(planes, row - rowBytes, planes, row, rowBytes);
                continue;
            }

            int flagPos = r.Position;
            r.Skip(flagBytes);

            for (int u = 0; u < units; u++)
            {
                int bitPos = u * 2;
                byte flagByte = r.Data[flagPos + bitPos / 8];
                int code = (flagByte >> (6 - (bitPos % 8))) & 0x3;
                int dst = row + u * bytesPerUnit;

                switch (code)
                {
                    case 0: // 圧縮なし
                        r.ReadBytes(planes, dst, bytesPerUnit);
                        break;
                    case 1: // 1 つ上の画素
                        if (y >= 1) Buffer.BlockCopy(planes, dst - rowBytes, planes, dst, bytesPerUnit);
                        break;
                    case 2: // 1 つ左の画素
                        if (u >= 1) Buffer.BlockCopy(planes, dst - bytesPerUnit, planes, dst, bytesPerUnit);
                        break;
                    default: // 4 つ上の画素
                        if (y >= 4) Buffer.BlockCopy(planes, dst - rowBytes * 4, planes, dst, bytesPerUnit);
                        break;
                }
            }
        }
        return planes;
    }

    /// <summary>プレーン形式をピクセル毎のパレット番号に変換する。</summary>
    private static byte[] ExpandPlanes(byte[] planes, int depth, int width, int height)
    {
        int units = (width + 7) / 8;
        int rowBytes = units * depth;
        var pixels = new byte[width * height];

        for (int y = 0; y < height; y++)
        {
            int row = y * rowBytes;
            int outRow = y * width;
            for (int u = 0; u < units; u++)
            {
                int unitOffset = row + u * depth;
                int x0 = u * 8;
                for (int b = 0; b < 8; b++)
                {
                    int x = x0 + b;
                    if (x >= width) break;
                    int shift = 7 - b;   // MSB が左端のドット
                    int index = 0;
                    for (int p = 0; p < depth; p++)
                        index |= ((planes[unitOffset + p] >> shift) & 1) << p;
                    pixels[outRow + x] = (byte)index;
                }
            }
        }
        return pixels;
    }

    /// <summary>X68000 形式のパレット (GGGGGRRRRRBBBBBI) を Color に変換する。</summary>
    public static Color ToColor(ushort palette)
    {
        int i = palette & 1;
        int g = (palette >> 11) & 0x1f;
        int rr = (palette >> 6) & 0x1f;
        int b = (palette >> 1) & 0x1f;
        return Color.FromArgb(Scale(rr, i), Scale(g, i), Scale(b, i));
    }

    private static int Scale(int value5, int lowBit)
    {
        int v6 = (value5 << 1) | lowBit;
        return v6 * 255 / 63;
    }

    public Bitmap ToBitmap()
    {
        var bmp = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        var colors = new int[Palette.Length];
        for (int i = 0; i < Palette.Length; i++) colors[i] = ToColor(Palette[i]).ToArgb();

        var rect = new Rectangle(0, 0, Width, Height);
        BitmapData bits = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var line = new int[Width];
            for (int y = 0; y < Height; y++)
            {
                int src = y * Width;
                for (int x = 0; x < Width; x++) line[x] = colors[PixelIndices[src + x]];
                System.Runtime.InteropServices.Marshal.Copy(line, 0, bits.Scan0 + y * bits.Stride, Width);
            }
        }
        finally
        {
            bmp.UnlockBits(bits);
        }
        return bmp;
    }

    private static readonly Encoding ShiftJis = CreateShiftJis();

    private static Encoding CreateShiftJis()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try { return Encoding.GetEncoding(932); }
        catch (ArgumentException) { return Encoding.Default; }
    }

    private sealed class Reader(byte[] data)
    {
        public byte[] Data { get; } = data;
        public int Position { get; private set; }

        private void Need(int count)
        {
            if (Position + count > Data.Length)
                throw new InvalidDataException("データが途中で終了しています（ファイルが壊れている可能性があります）。");
        }

        public byte PeekByte() { Need(1); return Data[Position]; }
        public byte ReadByte() { Need(1); return Data[Position++]; }
        public ushort ReadU16() { Need(2); return (ushort)((Data[Position++] << 8) | Data[Position++]); }
        public uint ReadU32() => ((uint)ReadU16() << 16) | ReadU16();
        public void Skip(int count) { Need(count); Position += count; }

        public void ReadBytes(byte[] destination, int destinationOffset, int count)
        {
            Need(count);
            Buffer.BlockCopy(Data, Position, destination, destinationOffset, count);
            Position += count;
        }
    }
}
