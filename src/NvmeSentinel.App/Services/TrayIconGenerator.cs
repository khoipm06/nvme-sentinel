using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace NvmeSentinel.App.Services;

public static class TrayIconGenerator
{
    public enum TrayState
    {
        Healthy,
        Warning,
        Critical
    }

    public static WindowIcon CreateIcon(TrayState state = TrayState.Healthy)
    {
        const int size = 64;
        using var bmp = new WriteableBitmap(
            new PixelSize(size, size),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);

        // Graphite & Signal Orange / Crimson hardware mark (never neon rainbow)
        var (rAcc, gAcc, bAcc) = state switch
        {
            TrayState.Critical => ((byte)217, (byte)43, (byte)33),   // #D92B21 Signal Red
            TrayState.Warning => ((byte)232, (byte)87, (byte)31),    // #E8571F Safety Orange
            _ => ((byte)239, (byte)240, (byte)241)                   // #EFF0F1 Crisp Ink
        };

        unsafe
        {
            using var fb = bmp.Lock();
            byte* ptr = (byte*)fb.Address;
            int stride = fb.RowBytes;

            for (int y = 0; y < size; y++)
            {
                byte* row = ptr + y * stride;
                for (int x = 0; x < size; x++)
                {
                    float dx = Math.Max(0, Math.Max(14 - x, x - 49));
                    float dy = Math.Max(0, Math.Max(14 - y, y - 49));
                    float dist = MathF.Sqrt(dx * dx + dy * dy);

                    if (x < 4 || x > 59 || y < 4 || y > 59 || dist > 10.5f)
                    {
                        row[x * 4 + 0] = 0;
                        row[x * 4 + 1] = 0;
                        row[x * 4 + 2] = 0;
                        row[x * 4 + 3] = 0;
                        continue;
                    }

                    float aa = dist > 9.5f ? Math.Clamp(10.5f - dist, 0f, 1f) : 1f;

                    // Neutral graphite body (#16171A)
                    byte br = 22, bg = 23, bb = 26;

                    // Precision horizontal telemetry bars + signal pip
                    if (y >= 20 && y <= 25 && x >= 16 && x <= 47)
                    {
                        br = rAcc; bg = gAcc; bb = bAcc;
                    }
                    else if (y >= 31 && y <= 35 && x >= 16 && x <= 38)
                    {
                        br = 142; bg = 145; bb = 150;
                    }
                    else if (y >= 41 && y <= 45 && x >= 16 && x <= 32)
                    {
                        br = 142; bg = 145; bb = 150;
                    }
                    else if (MathF.Sqrt((x - 44f) * (x - 44f) + (y - 43f) * (y - 43f)) <= 4.5f)
                    {
                        br = 232; bg = 87; bb = 31; // #E8571F pip
                    }

                    byte a = (byte)(255 * aa);
                    row[x * 4 + 0] = (byte)(bb * aa);
                    row[x * 4 + 1] = (byte)(bg * aa);
                    row[x * 4 + 2] = (byte)(br * aa);
                    row[x * 4 + 3] = a;
                }
            }
        }

        using var ms = new MemoryStream();
        bmp.Save(ms);
        ms.Position = 0;
        return new WindowIcon(ms);
    }
}
