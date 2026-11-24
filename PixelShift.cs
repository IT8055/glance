namespace Glance;

/// <summary>
/// OLED mode. An OLED pixel dims a little with every hour it is lit, so anything that sits still for a long
/// time (a label, a border, the end of a bar) slowly leaves its outline behind. The cure is the one OLED
/// monitors and TVs use themselves: shift the picture by a pixel now and then, so the wear is shared between
/// neighbouring pixels instead of landing on the same ones.
///
/// How far: the widget's thinnest details (text strokes, borders) are one or two pixels wide and its bars a
/// few pixels tall, so straying a handful of pixels is enough for no pixel to be lit all the time by a
/// stroke or an edge. Further than that adds nothing but visible movement.
///
/// How often: burn-in builds up over hundreds of hours, so what matters is that every position gets an equal
/// share of the time in the long run, not how quickly the widget moves. Monitors move the whole screen about
/// a pixel every few minutes. The widget is small and often shown briefly, so the default is a pixel a
/// minute, which is still too little to see happen.
///
/// The position is worked out from the clock alone. That way it carries on where it would have been when
/// the widget is hidden and shown again or Glance is restarted, and never goes back to the same starting
/// spot, which would wear that spot most.
/// </summary>
internal static class PixelShift
{
    public const int DefaultSeconds = 60;
    public const int DefaultTravel = 8;

    /// <summary>
    /// Where the widget sits inside its box of (travel + 1) by (travel + 1) positions at a given moment, each
    /// coordinate from 0 to travel. The path snakes through every position, a row at a time, then back again,
    /// so each move is one pixel left, right, up or down, and every position gets the same share of the time.
    /// </summary>
    public static (int X, int Y) At(DateTime utc, int seconds, int travel)
    {
        seconds = Math.Max(1, seconds);
        travel = Math.Max(1, travel);
        var side = travel + 1;
        var cells = side * side;

        // There and back takes 2 x cells steps. Each end of the path is held for two steps running, which is
        // what gives the two end positions the same share as the rest.
        var step = (int)(utc.Ticks / TimeSpan.TicksPerSecond / seconds % (2L * cells));
        var cell = step < cells ? step : 2 * cells - 1 - step;
        var row = cell / side;
        var along = cell % side;
        return (row % 2 == 0 ? along : travel - along, row);
    }

    /// <summary>
    /// The same position as an offset in screen pixels for a widget pinned to a corner or edge. A pinned
    /// widget only ever moves inwards, away from the screen edge it is pinned to, so it never slides off
    /// screen. One placed by hand moves evenly around the spot it was put.
    /// </summary>
    public static (int X, int Y) Offset(DateTime utc, int seconds, int travel, string anchor)
    {
        travel = Math.Max(1, travel);
        var (x, y) = At(utc, seconds, travel);
        if (anchor == Anchors.Custom) return (x - travel / 2, y - travel / 2);

        var dx = anchor.EndsWith("left", StringComparison.Ordinal) ? x
               : anchor.EndsWith("right", StringComparison.Ordinal) ? -x
               : x - travel / 2;
        var dy = anchor.StartsWith("bottom", StringComparison.Ordinal) ? -y : y;
        return (dx, dy);
    }
}
