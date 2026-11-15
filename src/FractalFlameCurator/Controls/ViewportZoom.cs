namespace FractalFlameCurator.Controls;

public static class ViewportZoom
{
    public const double Minimum = 0.001;
    public const double Maximum = 8;

    public static double Clamp(double scale) => Math.Clamp(scale, Minimum, Maximum);

    public static double Fit(double viewportWidth, double viewportHeight, int imageWidth, int imageHeight) =>
        Clamp(Math.Min(Math.Max(1, viewportWidth) / imageWidth, Math.Max(1, viewportHeight) / imageHeight));
}
