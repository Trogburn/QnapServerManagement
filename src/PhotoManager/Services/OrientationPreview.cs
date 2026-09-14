using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoManager.Models;

namespace PhotoManager.Services;

internal static class OrientationPreview
{
    public static ImageSource? TryLoad(string path, int? orientation)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.Rotation = orientation switch
        {
            3 => Rotation.Rotate180,
            6 => Rotation.Rotate90,
            8 => Rotation.Rotate270,
            _ => Rotation.Rotate0
        };
        image.EndInit();
        image.Freeze();

        if (orientation is not (2 or 4 or 5 or 7))
        {
            return image;
        }

        var transform = orientation switch
        {
            2 => new ScaleTransform(-1, 1),
            4 => new ScaleTransform(1, -1),
            5 => new TransformGroup
            {
                Children = { new ScaleTransform(-1, 1), new RotateTransform(270) }
            },
            7 => new TransformGroup
            {
                Children = { new ScaleTransform(-1, 1), new RotateTransform(90) }
            },
            _ => Transform.Identity
        };
        var transformed = new TransformedBitmap(image, transform);
        transformed.Freeze();
        return transformed;
    }
}
