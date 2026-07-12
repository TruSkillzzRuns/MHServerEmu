using System;
using DDSLib.Constants;

namespace DDSLib.Compression;

internal static class DdsBc5
{
    public static byte[] CompressImage(byte[] rgba, int width, int height)
    {
        int blockCount = ((width + 3) / 4) * ((height + 3) / 4);
        byte[] blocks = new byte[blockCount * 16];

        for (int by = 0; by < height; by += 4)
        {
            for (int bx = 0; bx < width; bx += 4)
            {
                int blockIndex = ((by / 4) * ((width + 3) / 4) + (bx / 4)) * 16;
                EncodeChannelBlock(rgba, width, height, bx, by, 0, blocks.AsSpan(blockIndex, 8));
                EncodeChannelBlock(rgba, width, height, bx, by, 1, blocks.AsSpan(blockIndex + 8, 8));
            }
        }

        return blocks;
    }

    public static byte[] DecompressImage(int width, int height, byte[] blocks)
    {
        byte[] dest = new byte[width * height * 4];

        for (int by = 0; by < height; by += 4)
        {
            for (int bx = 0; bx < width; bx += 4)
            {
                int blockIndex = ((by / 4) * ((width + 3) / 4) + (bx / 4)) * 16;
                byte[] red = DecodeChannelBlock(blocks.AsSpan(blockIndex, 8));
                byte[] green = DecodeChannelBlock(blocks.AsSpan(blockIndex + 8, 8));

                for (int py = 0; py < 4; py++)
                {
                    for (int px = 0; px < 4; px++)
                    {
                        int x = bx + px;
                        int y = by + py;
                        if (x >= width || y >= height)
                            continue;

                        int srcIndex = py * 4 + px;
                        int dstIndex = (y * width + x) * 4;
                        dest[dstIndex + 0] = red[srcIndex];
                        dest[dstIndex + 1] = green[srcIndex];
                        dest[dstIndex + 2] = 255;
                        dest[dstIndex + 3] = 255;
                    }
                }
            }
        }

        return dest;
    }

    private static void EncodeChannelBlock(byte[] rgba, int width, int height, int startX, int startY, int channelOffset, Span<byte> output)
    {
        byte[] values = new byte[16];
        bool[] valid = new bool[16];
        int index = 0;

        for (int py = 0; py < 4; py++)
        {
            for (int px = 0; px < 4; px++)
            {
                int x = startX + px;
                int y = startY + py;
                if (x < width && y < height)
                {
                    int src = (y * width + x) * 4 + channelOffset;
                    values[index] = rgba[src];
                    valid[index] = true;
                }
                index++;
            }
        }

        int min5 = 255, max5 = 0, min7 = 255, max7 = 0;
        for (int i = 0; i < 16; i++)
        {
            if (!valid[i])
                continue;

            int value = values[i];
            if (value < min7) min7 = value;
            if (value > max7) max7 = value;
            if (value != 0 && value < min5) min5 = value;
            if (value != 255 && value > max5) max5 = value;
        }

        if (min5 > max5) min5 = max5;
        if (min7 > max7) min7 = max7;
        FixRange(ref min5, ref max5, 5);
        FixRange(ref min7, ref max7, 7);

        byte[] codes5 = BuildCodes5(min5, max5);
        byte[] codes7 = BuildCodes7(min7, max7);

        byte[] indices5 = new byte[16];
        byte[] indices7 = new byte[16];
        int err5 = FitCodes(values, valid, codes5, indices5);
        int err7 = FitCodes(values, valid, codes7, indices7);

        if (err5 <= err7)
            WriteAlphaBlock5(min5, max5, indices5, output);
        else
            WriteAlphaBlock7(min7, max7, indices7, output);
    }

    private static byte[] DecodeChannelBlock(ReadOnlySpan<byte> block)
    {
        int alpha0 = block[0];
        int alpha1 = block[1];
        byte[] codes = new byte[8];
        codes[0] = (byte)alpha0;
        codes[1] = (byte)alpha1;

        if (alpha0 <= alpha1)
        {
            for (int i = 1; i < 5; i++)
                codes[1 + i] = (byte)(((5 - i) * alpha0 + i * alpha1) / 5);
            codes[6] = 0;
            codes[7] = 255;
        }
        else
        {
            for (int i = 1; i < 7; i++)
                codes[1 + i] = (byte)(((7 - i) * alpha0 + i * alpha1) / 7);
        }

        byte[] indices = new byte[16];
        int src = 2;
        for (int group = 0; group < 2; group++)
        {
            int packed = block[src++] | (block[src++] << 8) | (block[src++] << 16);
            for (int j = 0; j < 8; j++)
                indices[group * 8 + j] = (byte)((packed >> (3 * j)) & 0x7);
        }

        byte[] values = new byte[16];
        for (int i = 0; i < 16; i++)
            values[i] = codes[indices[i]];

        return values;
    }

    private static byte[] BuildCodes5(int min, int max)
    {
        byte[] codes = new byte[8];
        codes[0] = (byte)min;
        codes[1] = (byte)max;
        for (int i = 1; i < 5; i++)
            codes[1 + i] = (byte)(((5 - i) * min + i * max) / 5);
        codes[6] = 0;
        codes[7] = 255;
        return codes;
    }

    private static byte[] BuildCodes7(int min, int max)
    {
        byte[] codes = new byte[8];
        codes[0] = (byte)min;
        codes[1] = (byte)max;
        for (int i = 1; i < 7; i++)
            codes[1 + i] = (byte)(((7 - i) * min + i * max) / 7);
        return codes;
    }

    private static int FitCodes(byte[] values, bool[] valid, byte[] codes, byte[] indices)
    {
        int err = 0;
        for (int i = 0; i < 16; i++)
        {
            if (!valid[i])
            {
                indices[i] = 0;
                continue;
            }

            int value = values[i];
            int least = int.MaxValue;
            int index = 0;
            for (int j = 0; j < 8; j++)
            {
                int dist = value - codes[j];
                dist *= dist;
                if (dist < least)
                {
                    least = dist;
                    index = j;
                }
            }

            indices[i] = (byte)index;
            err += least;
        }

        return err;
    }

    private static void FixRange(ref int min, ref int max, int steps)
    {
        if (max - min < steps)
            max = Math.Min(min + steps, 255);

        if (max - min < steps)
            min = Math.Max(0, max - steps);
    }

    private static void WriteAlphaBlock5(int alpha0, int alpha1, byte[] indices, Span<byte> block)
    {
        if (alpha0 > alpha1)
        {
            byte[] swapped = new byte[16];
            for (int i = 0; i < 16; i++)
            {
                byte index = indices[i];
                swapped[i] = index switch
                {
                    0 => (byte)1,
                    1 => (byte)0,
                    <= 5 => (byte)(7 - index),
                    _ => index
                };
            }

            WriteAlphaBlock(alpha1, alpha0, swapped, block);
        }
        else
        {
            WriteAlphaBlock(alpha0, alpha1, indices, block);
        }
    }

    private static void WriteAlphaBlock7(int alpha0, int alpha1, byte[] indices, Span<byte> block)
    {
        if (alpha0 < alpha1)
        {
            byte[] swapped = new byte[16];
            for (int i = 0; i < 16; i++)
            {
                byte index = indices[i];
                swapped[i] = index switch
                {
                    0 => (byte)1,
                    1 => (byte)0,
                    _ => (byte)(9 - index)
                };
            }

            WriteAlphaBlock(alpha1, alpha0, swapped, block);
        }
        else
        {
            WriteAlphaBlock(alpha0, alpha1, indices, block);
        }
    }

    private static void WriteAlphaBlock(int alpha0, int alpha1, byte[] indices, Span<byte> block)
    {
        block[0] = (byte)alpha0;
        block[1] = (byte)alpha1;

        int dest = 2;
        for (int group = 0; group < 2; group++)
        {
            int value = 0;
            for (int j = 0; j < 8; j++)
            {
                int index = indices[group * 8 + j];
                value |= index << (3 * j);
            }

            for (int j = 0; j < 3; j++)
                block[dest++] = (byte)((value >> (8 * j)) & 0xff);
        }
    }
}
