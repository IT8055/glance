using System.Windows;
using System.Windows.Media;

namespace Glance;

/// <summary>
/// A colour scheme for the widget. Colours are "#RRGGBB".
/// </summary>
/// <param name="Window">Background of the widget, seen between and around the panels.</param>
/// <param name="Panel">Background of each section's panel.</param>
/// <param name="PanelBorder">Outline of each panel, and of the widget.</param>
/// <param name="Heading">Section headings.</param>
/// <param name="Label">Names of the main readings.</param>
/// <param name="Value">Values.</param>
/// <param name="Dim">Names of the compact readings, and the text beside headings.</param>
/// <param name="Faint">Small print: reset times, lowest and highest.</param>
/// <param name="Track">The unfilled part of a bar.</param>
/// <param name="Accent">Bars and graphs at a normal level.</param>
public sealed record Theme(
    string Name, string Window, string Panel, string PanelBorder, string Heading, string Label, string Value,
    string Dim, string Faint, string Track, string Accent, string Warning, string Critical);

public static class Themes
{
    public const string Default = "Midnight";

    public static readonly IReadOnlyList<Theme> All =
    [
        new("Midnight", "#0E1014", "#171A20", "#2A2E38", "#8B919D", "#D8DCE4", "#FFFFFF", "#9BA1AD", "#7A808C", "#2A2E38", "#6FB1FF", "#F2B04A", "#F0616D"),
        new("Graphite", "#0F0F0F", "#1A1A1A", "#2E2E2E", "#8C8C8C", "#DCDCDC", "#FFFFFF", "#9C9C9C", "#7A7A7A", "#2E2E2E", "#4FD1C5", "#F2B04A", "#F0616D"),
        new("Ocean", "#09131D", "#0F1F2E", "#1E3549", "#7FA3C2", "#D6E6F5", "#FFFFFF", "#8FB0CC", "#6C8BA6", "#1C3246", "#38BDF8", "#FBBF24", "#FB7185"),
        new("Forest", "#0C1310", "#13201A", "#24352B", "#86A894", "#DCEBE2", "#FFFFFF", "#94B3A1", "#6F8C7C", "#22332A", "#5FD38D", "#E9C46A", "#F07167"),
        new("Ember", "#151010", "#201817", "#3A2A26", "#B39A92", "#F0E2DD", "#FFFFFF", "#BBA39B", "#94807A", "#33241F", "#FF9361", "#FFC857", "#FF5D73"),
        new("Violet", "#110F1A", "#1B1828", "#302A45", "#9D94BD", "#E3DFF2", "#FFFFFF", "#A79FC4", "#807A9C", "#2A2540", "#A78BFA", "#F5C26B", "#FB7185"),
        new("Light", "#E6E9EF", "#FFFFFF", "#D2D7E0", "#667080", "#2B303A", "#11141A", "#596170", "#78808E", "#E1E5EB", "#2F6FE4", "#B86E00", "#D43C4E"),
        new("High contrast", "#000000", "#000000", "#FFFFFF", "#FFFFFF", "#FFFFFF", "#FFFFFF", "#E6E6E6", "#CCCCCC", "#404040", "#00E5FF", "#FFD400", "#FF6B6B"),

        // For OLED screens: black is a pixel switched off, which neither wears nor uses power, and the text is
        // kept below full brightness because wear grows with brightness.
        new("OLED black", "#000000", "#000000", "#1F1F1F", "#7C7C7C", "#B4B4B4", "#D6D6D6", "#8A8A8A", "#6C6C6C", "#1A1A1A", "#4A9BE0", "#D69A3C", "#D9535F"),
    ];

    public static Theme Find(string? name) => All.FirstOrDefault(t => t.Name == name) ?? All[0];

    /// <summary>
    /// Puts the theme's colours into a window's resources. The widget's XAML refers to them by name
    /// (AccentBrush and so on), so everything on screen recolours at once.
    /// </summary>
    public static void Apply(ResourceDictionary resources, Theme theme)
    {
        resources["WindowBrush"] = Brush(theme.Window);
        resources["PanelBrush"] = Brush(theme.Panel);
        resources["PanelBorderBrush"] = Brush(theme.PanelBorder);
        resources["HeadingBrush"] = Brush(theme.Heading);
        resources["LabelBrush"] = Brush(theme.Label);
        resources["ValueBrush"] = Brush(theme.Value);
        resources["DimBrush"] = Brush(theme.Dim);
        resources["FaintBrush"] = Brush(theme.Faint);
        resources["TrackBrush"] = Brush(theme.Track);
        resources["AccentBrush"] = Brush(theme.Accent);
        resources["WarningBrush"] = Brush(theme.Warning);
        resources["CriticalBrush"] = Brush(theme.Critical);
    }

    private static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
