using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MiMi.App.Interop;

/// <summary>One MiMi identity for the executable, taskbar windows, and notification area.</summary>
public static class MiMiIcon
{
    private static readonly Lazy<ImageSource> SharedImage = new(LoadImage, LazyThreadSafetyMode.ExecutionAndPublication);

    public static ImageSource ImageSource => SharedImage.Value;

    public static void Apply(Window window) => window.Icon = ImageSource;

    private static ImageSource LoadImage()
    {
        var stream = Application.GetResourceStream(new Uri(
            "pack://application:,,,/MiMi;component/Assets/mimi.ico")).Stream;
        using (stream)
        {
            // BitmapImage picks the ICO's first (16 px) frame. Decode the container and
            // select 64 px so normal/high-DPI taskbars downscale crisp artwork instead.
            var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            var frame = decoder.Frames.FirstOrDefault(candidate => candidate.PixelWidth == 64)
                        ?? decoder.Frames.Last();
            frame.Freeze();
            return frame;
        }
    }

    /// <summary>32-pixel GDI icon for Shell_NotifyIconW, matching the embedded taskbar artwork.</summary>
    public static IntPtr CreateIconHandle()
    {
        const int size = 32;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var fill = new SolidColorBrush(Color.FromRgb(0x14, 0x12, 0x10));
            var stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0xC2, 0x66));
            dc.DrawRoundedRectangle(fill, new Pen(stroke, 1.6), new Rect(1.5, 1.5, 29, 29), 8, 8);
            var eye = new SolidColorBrush(Color.FromRgb(0xFF, 0xF7, 0xE8));
            dc.DrawEllipse(eye, null, new Point(11.5, 15.5), 2.8, 3.6);
            dc.DrawEllipse(eye, null, new Point(20.5, 15.5), 2.8, 3.6);
            var glow = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xB4, 0x5C));
            dc.DrawRoundedRectangle(null, new Pen(glow, 1), new Rect(5.5, 25, 21, 2), 1, 1);
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var stride = size * 4;
        var pixels = new byte[stride * size];
        bitmap.CopyPixels(pixels, stride, 0);
        var hColor = NativeMethods.CreateBitmap(size, size, 1, 32, pixels);
        var hMask = NativeMethods.CreateBitmap(size, size, 1, 1, new byte[(size / 8) * size]);
        try
        {
            var info = new NativeMethods.ICONINFO { fIcon = true, hbmMask = hMask, hbmColor = hColor };
            return NativeMethods.CreateIconIndirect(ref info);
        }
        finally
        {
            NativeMethods.DeleteObject(hColor);
            NativeMethods.DeleteObject(hMask);
        }
    }
}
