using System.Drawing;
using System.Drawing.Imaging;

namespace MitViewer;

/// <summary>
/// MAKIchan graphics (.mag) 形式のデコーダ。16色／256色の MAKI02 形式に対応する。
/// </summary>
public static class MagImage
{
    private static readonly byte[] Signature = "MAKI02  "u8.ToArray();

    // コピー元の相対位置（CopyX はバイト単位、CopyY はライン単位）。インデックス0は「新規ピクセル読み込み」を表す。
    private static readonly int[] CopyX = { 0, 2, 4, 8, 0, 2, 0, 2, 4, 0, 2, 4, 0, 2, 4, 0 };
    private static readonly int[] CopyY = { 0, 0, 0, 0, 1, 1, 2, 2, 2, 4, 4, 4, 8, 8, 8, 16 };

    public static Bitmap Load(string path) => Decode(File.ReadAllBytes(path));

    public static Bitmap Decode(byte[] data)
    {
        if (data.Length < 32 || !data.AsSpan(0, Signature.Length).SequenceEqual(Signature))
            throw new InvalidDataException("マジックナンバ 'MAKI02  ' がありません。.mag ファイルではありません。");

        int eof = Array.IndexOf(data, (byte)0x1a, Signature.Length);
        if (eof < 0) throw new InvalidDataException("コメント終端 (0x1a) が見つかりません。");
        int header = eof + 1;
        if (header + 32 > data.Length) throw new InvalidDataException("ヘッダが途中で終わっています。");

        int screenMode = data[header + 2];
        bool is256 = (screenMode & 0x80) != 0;

        int x1 = ReadU16(data, header + 4);
        int y1 = ReadU16(data, header + 6);
        int x2 = ReadU16(data, header + 8);
        int y2 = ReadU16(data, header + 10);
        int flagAOffset = ReadI32(data, header + 12);
        int flagBOffset = ReadI32(data, header + 16);
        int flagBSize = ReadI32(data, header + 20);
        int pixelOffset = ReadI32(data, header + 24);
        int pixelSize = ReadI32(data, header + 28);

        int align = is256 ? 4 : 8;
        x1 -= x1 % align;
        int width = x2 - x1 + 1;
        if (width % align != 0) width += align - width % align;
        int height = y2 - y1 + 1;
        if (width <= 0 || height <= 0 || width > 8192 || height > 8192)
            throw new InvalidDataException($"不正な画像サイズです: {width}x{height}");

        int paletteCount = is256 ? 256 : 16;
        int paletteOffset = header + 32;
        if (paletteOffset + paletteCount * 3 > data.Length)
            throw new InvalidDataException("パレットが途中で終わっています。");
        var palette = new Color[paletteCount];
        for (int i = 0; i < paletteCount; i++)
        {
            int p = paletteOffset + i * 3;
            palette[i] = Color.FromArgb(data[p + 1], data[p], data[p + 2]); // 格納順は G, R, B
        }

        int bytesPerLine = is256 ? width : width / 2;
        int flagBytesPerLine = bytesPerLine / 4; // フラグ1バイト = ニブル2つ = ピクセルデータ4バイト
        if (flagBytesPerLine <= 0) throw new InvalidDataException("画像の横幅が小さすぎます。");

        var flagA = new Reader(data, header + flagAOffset, flagBOffset - flagAOffset);
        var flagB = new Reader(data, header + flagBOffset, flagBSize);
        var pixels = new Reader(data, header + pixelOffset, pixelSize);

        var raw = new byte[bytesPerLine * height];
        var flags = new byte[flagBytesPerLine];
        int flagABit = 0;
        byte flagAByte = 0;

        for (int y = 0; y < height; y++)
        {
            for (int i = 0; i < flagBytesPerLine; i++)
            {
                if (flagABit == 0) { flagAByte = flagA.ReadByte(); flagABit = 8; }
                flagABit--;
                bool hasFlag = ((flagAByte >> flagABit) & 1) != 0;
                if (hasFlag) flags[i] ^= flagB.ReadByte(); // 直前のラインのフラグとの差分
            }

            int lineStart = y * bytesPerLine;
            for (int i = 0; i < flagBytesPerLine; i++)
            {
                for (int half = 0; half < 2; half++)
                {
                    int code = half == 0 ? flags[i] >> 4 : flags[i] & 0x0f;
                    int dest = lineStart + (i * 2 + half) * 2; // 1ニブル = ピクセルデータ2バイト
                    if (code == 0)
                    {
                        for (int k = 0; k < 2; k++) raw[dest + k] = pixels.ReadByte();
                    }
                    else
                    {
                        int src = dest - CopyY[code] * bytesPerLine - CopyX[code];
                        if (src < 0)
                            throw new InvalidDataException("コピー元が画像範囲外です。壊れた .mag ファイルの可能性があります。");
                        for (int k = 0; k < 2; k++) raw[dest + k] = raw[src + k];
                    }
                }
            }
        }

        return ToBitmap(raw, width, height, bytesPerLine, is256, palette);
    }

    private static Bitmap ToBitmap(byte[] raw, int width, int height, int bytesPerLine, bool is256, Color[] palette)
    {
        var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var argbPalette = new int[palette.Length];
        for (int i = 0; i < palette.Length; i++) argbPalette[i] = palette[i].ToArgb();

        BitmapData bits = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var line = new int[width];
            for (int y = 0; y < height; y++)
            {
                int rowStart = y * bytesPerLine;
                for (int x = 0; x < width; x++)
                {
                    int index = is256
                        ? raw[rowStart + x]
                        : (x % 2 == 0 ? raw[rowStart + x / 2] >> 4 : raw[rowStart + x / 2] & 0x0f);
                    line[x] = argbPalette[index];
                }
                System.Runtime.InteropServices.Marshal.Copy(line, 0, bits.Scan0 + y * bits.Stride, width);
            }
        }
        finally
        {
            bmp.UnlockBits(bits);
        }
        return bmp;
    }

    private static int ReadU16(byte[] d, int p) => d[p] | (d[p + 1] << 8);

    private static int ReadI32(byte[] d, int p) => d[p] | (d[p + 1] << 8) | (d[p + 2] << 16) | (d[p + 3] << 24);

    /// <summary>指定範囲を順に読み出す。範囲外を読もうとした場合は壊れたファイルとして扱う。</summary>
    private sealed class Reader
    {
        private readonly byte[] _data;
        private readonly int _end;
        private int _position;

        public Reader(byte[] data, int start, int length)
        {
            if (start < 0 || length < 0 || start > data.Length || start + length > data.Length)
                throw new InvalidDataException("データ範囲がファイルサイズを超えています。");
            _data = data;
            _position = start;
            _end = start + length;
        }

        public byte ReadByte()
        {
            if (_position >= _end) throw new InvalidDataException("データが途中で終わっています。壊れた .mag ファイルの可能性があります。");
            return _data[_position++];
        }
    }
}
