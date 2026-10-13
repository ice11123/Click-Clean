using System.Windows.Media.Imaging;

namespace ClickClean;

/// <summary>缓存细颗粒层，供应用内磨砂卡片复用；不采集桌面或启动刷新循环。</summary>
internal static class FrostedMaterial
{
    internal static readonly Brush LightGrain = CreateGrain(false);
    internal static readonly Brush DarkGrain = CreateGrain(true);

    private static Brush CreateGrain(bool lightPixels)
    {
        const int size = 64;
        var pixels = new byte[size * size * 4];
        var random = new Random(741);
        for (var i = 0; i < pixels.Length; i += 4) {
            var alpha = (byte)random.Next(0, 7);
            pixels[i] = pixels[i + 1] = pixels[i + 2] = lightPixels ? alpha : (byte)0;
            pixels[i + 3] = alpha;
        }
        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Pbgra32, null, pixels, size * 4);
        bitmap.Freeze();
        var brush = new ImageBrush(bitmap) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, size, size), Stretch = Stretch.None };
        brush.Freeze(); return brush;
    }
}
