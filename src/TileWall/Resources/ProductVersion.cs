namespace TileWall.Resources;

/// <summary>The public release label is derived from the assembly's numeric version.</summary>
internal static class ProductVersion
{
    internal static string Display
    {
        get
        {
            var version = typeof(ProductVersion).Assembly.GetName().Version!;
            return FormattableString.Invariant($"V{version.Major}.{version.Minor * 10 + version.Build:D2}");
        }
    }
}
