namespace GithubDash.App.Services;

/// <summary>
/// Draws the taskbar badge (a red disc with the count in white) straight into a BGRA pixel buffer.
/// It needs no graphics library, so it behaves the same everywhere and can be unit tested.
/// </summary>
public static class BadgePixels
{
    // 5x7 digit font; each row is 5 bits, most significant bit = leftmost pixel.
    private static readonly byte[][] Digits =
    [
        [0b01110, 0b10001, 0b10011, 0b10101, 0b11001, 0b10001, 0b01110], // 0
        [0b00100, 0b01100, 0b00100, 0b00100, 0b00100, 0b00100, 0b01110], // 1
        [0b01110, 0b10001, 0b00001, 0b00010, 0b00100, 0b01000, 0b11111], // 2
        [0b11110, 0b00001, 0b00001, 0b01110, 0b00001, 0b00001, 0b11110], // 3
        [0b00010, 0b00110, 0b01010, 0b10010, 0b11111, 0b00010, 0b00010], // 4
        [0b11111, 0b10000, 0b11110, 0b00001, 0b00001, 0b10001, 0b01110], // 5
        [0b00110, 0b01000, 0b10000, 0b11110, 0b10001, 0b10001, 0b01110], // 6
        [0b11111, 0b00001, 0b00010, 0b00100, 0b01000, 0b01000, 0b01000], // 7
        [0b01110, 0b10001, 0b10001, 0b01110, 0b10001, 0b10001, 0b01110], // 8
        [0b01110, 0b10001, 0b10001, 0b01111, 0b00001, 0b00010, 0b01100], // 9
    ];

    /// <summary>The text shown for a count: the number itself, capped at 99.</summary>
    public static string Label(int count) => Math.Min(Math.Max(count, 0), 99).ToString();

    /// <summary>Renders a <paramref name="size"/> × <paramref name="size"/> badge as premultiplied-free BGRA bytes (top row first).</summary>
    public static byte[] Render(int count, int size = 32)
    {
        var pixels = new byte[size * size * 4];
        var label = Label(count);
        double centre = (size - 1) / 2.0, radius = size / 2.0 - 0.5;

        // Anti-aliased disc: coverage falls off over the last pixel of the radius.
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var distance = Math.Sqrt((x - centre) * (x - centre) + (y - centre) * (y - centre));
                var coverage = Math.Clamp(radius - distance + 0.5, 0, 1);
                if (coverage <= 0) continue;
                Set(pixels, size, x, y, 0x3B, 0x30, 0xE0, (byte)Math.Round(coverage * 255)); // red (B, G, R, A)
            }
        }

        // Digits: scale so that two of them fit comfortably inside the disc.
        var scale = label.Length == 1 ? 3 : 2;
        var glyphWidth = 5 * scale;
        var gap = scale;
        var textWidth = label.Length * glyphWidth + (label.Length - 1) * gap;
        var left = (size - textWidth) / 2;
        var top = (size - 7 * scale) / 2;

        for (var i = 0; i < label.Length; i++)
        {
            var glyph = Digits[label[i] - '0'];
            var originX = left + i * (glyphWidth + gap);
            for (var row = 0; row < 7; row++)
            for (var col = 0; col < 5; col++)
            {
                if ((glyph[row] & (1 << (4 - col))) == 0) continue;
                for (var dy = 0; dy < scale; dy++)
                for (var dx = 0; dx < scale; dx++)
                    Set(pixels, size, originX + col * scale + dx, top + row * scale + dy, 0xFF, 0xFF, 0xFF, 0xFF);
            }
        }

        return pixels;
    }

    private static void Set(byte[] pixels, int size, int x, int y, byte b, byte g, byte r, byte a)
    {
        if (x < 0 || y < 0 || x >= size || y >= size) return;
        var i = (y * size + x) * 4;
        pixels[i] = b;
        pixels[i + 1] = g;
        pixels[i + 2] = r;
        pixels[i + 3] = a;
    }
}
