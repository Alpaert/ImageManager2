namespace ImageManager.App.Services;

internal readonly record struct DetailPaneLayout(double GalleryWidth, double PaneWidth, double SplitterWidth, bool IsOverlay)
{
    public const double DefaultWidth = 312;
    public const double MinimumWidth = 248;
    public const double MaximumWidth = 520;
    public const double KeyboardStep = 16;

    public static double NormalizePreferredWidth(double width) =>
        double.IsFinite(width) ? Math.Clamp(width, MinimumWidth, MaximumWidth) : DefaultWidth;

    public static double AdjustPreferredWidth(double clientWidth, double sidebarWidth, double preferredWidth,
        double requestedWidth, bool regionSearch = false)
    {
        var oldLayout = Calculate(clientWidth, sidebarWidth, true, preferredWidth, regionSearch);
        var newLayout = Calculate(clientWidth, sidebarWidth, true, requestedWidth, regionSearch);
        return Math.Abs(oldLayout.PaneWidth - newLayout.PaneWidth) < 0.5
            ? preferredWidth : NormalizePreferredWidth(requestedWidth);
    }

    public static DetailPaneLayout Calculate(double clientWidth, double sidebarWidth, bool open, double preferredWidth,
        bool regionSearch = false)
    {
        var workspaceWidth = Math.Max(0, clientWidth - sidebarWidth);
        if (!open)
            return new(workspaceWidth, 0, 0, false);

        var width = NormalizePreferredWidth(preferredWidth);
        const double splitterWidth = 7;
        if (regionSearch)
        {
            // Leave room for both the input editor and the result gallery.
            var dockedWidth = Math.Min(width, workspaceWidth / 3);
            var splitter = Math.Min(splitterWidth, workspaceWidth - dockedWidth);
            return new(workspaceWidth - dockedWidth - splitter, dockedWidth, splitter, false);
        }

        if (clientWidth <= 720)
            return new(workspaceWidth, Math.Min(width, workspaceWidth), 0, true);

        const double minimumGalleryWidth = 270;
        var paneWidth = Math.Min(width, Math.Max(0, workspaceWidth - minimumGalleryWidth - splitterWidth));
        return new(workspaceWidth - paneWidth - splitterWidth, paneWidth, splitterWidth, false);
    }
}
