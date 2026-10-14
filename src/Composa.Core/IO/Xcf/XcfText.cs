using System.Globalization;
using System.Text;
using Composa.Editing;
using Composa.Model;
using Composa.Text;
using SkiaSharp;

namespace Composa.IO.Xcf;

/// <summary>
/// GIMP keeps what a text layer says in its <c>gimp-text-layer</c> parasite: a list of forms, one setting each, as
/// <c>(text "…")</c>, <c>(font "Sans Bold")</c>, <c>(font-size 32)</c>, <c>(font-size-unit pixels)</c>, <c>(color
/// (color-rgba 0 0 0 1))</c>, <c>(justify left)</c>, <c>(box-mode fixed)</c> with a width, a height and a unit,
/// <c>(letter-spacing 0)</c> and <c>(line-spacing 0)</c>. Text in one style maps onto <see cref="TextStyle"/> and is
/// retyped here; text with markup (several fonts or colors in one layer) keeps its pixels and says so. Written from
/// what GIMP's own files hold; nothing here comes from GIMP's code.
/// </summary>
internal static class XcfText
{
    public const string RasterizedNote = "文字已按像素导入，无法在此重新键入。";
    public const string MarkupNote = "文字混合了多种字体或颜色，已按像素导入，无法在此重新键入。";

    public sealed record Source(TextStyle Style, List<string> Notes);

    /// <summary>The style the parasite describes, or null when the layer has to stay pixels (with the reason in <paramref name="note"/>).</summary>
    public static Source? Parse(byte[] parasite, double resolution, out string note)
    {
        note = RasterizedNote;
        List<object> forms;
        try { forms = Expressions.Parse(Encoding.UTF8.GetString(parasite)); }
        catch (FormatException) { return null; }

        string? text = null, font = null, sizeUnit = "pixels", justify = "left", boxMode = "dynamic", boxUnit = "pixels";
        double size = 0, boxWidth = 0, boxHeight = 0, letterSpacing = 0, lineSpacing = 0;
        uint color = 0xFF000000;
        var markup = false;
        foreach (var form in forms)
        {
            if (form is not List<object> { Count: > 0 } list || list[0] is not Symbol head) continue;
            var value = list.Count > 1 ? list[1] : null;
            switch (head.Name)
            {
                case "text": text = value as string; break;
                case "markup": markup = true; break;
                case "font": font = FontName(list); break;
                case "font-size": size = Number(value); break;
                case "font-size-unit": sizeUnit = (value as Symbol)?.Name ?? sizeUnit; break;
                case "color": color = Color(list) ?? color; break;
                case "justify": justify = (value as Symbol)?.Name ?? justify; break;
                case "box-mode": boxMode = (value as Symbol)?.Name ?? boxMode; break;
                case "box-width": boxWidth = Number(value); break;
                case "box-height": boxHeight = Number(value); break;
                case "box-unit": boxUnit = (value as Symbol)?.Name ?? boxUnit; break;
                case "letter-spacing": letterSpacing = Number(value); break;
                case "line-spacing": lineSpacing = Number(value); break;
            }
        }
        if (markup) { note = MarkupNote; return null; }
        if (text == null || size <= 0) return null;

        var notes = new List<string>();
        var pixels = Pixels(size, sizeUnit, resolution);
        var (family, bold, italic) = Face(font ?? "");
        var alignment = justify switch { "right" => TextAlignment.Right, "center" => TextAlignment.Center, _ => TextAlignment.Left };
        if (justify == "fill") notes.Add("两端对齐的文字在此按左对齐排布。");
        var style = new TextStyle
        {
            Text = text.Length > TextStyle.MaxLength ? text[..TextStyle.MaxLength] : text,
            FontFamily = family, Bold = bold, Italic = italic, Size = pixels, Color = color, Alignment = alignment,
            Tracking = Pixels(letterSpacing, sizeUnit, resolution),
            // GIMP adds its line spacing to the font's own line height; here Leading is the whole baseline distance.
            Leading = lineSpacing != 0 ? pixels * 1.2 + Pixels(lineSpacing, sizeUnit, resolution) : 0,
            BoxWidth = boxMode == "fixed" && boxWidth > 0 ? Pixels(boxWidth, boxUnit, resolution) : null,
            BoxHeight = boxMode == "fixed" && boxHeight > 0 ? Pixels(boxHeight, boxUnit, resolution) : null
        };
        if (!EditorSession.FontFamilies.Contains(family, StringComparer.OrdinalIgnoreCase)) notes.Add($"字体「{family}」未安装；在安装之前，文字将以回退字体显示。");
        return new Source(style.Clamped(), notes);
    }

    /// <summary>The text laid out fresh from its style, placed where GIMP had the layer; null when the layout is more than the budget holds.</summary>
    public static Layer? Place(Source source, XcfLayer record, string name, ref long remainingPixels)
    {
        var layout = new TextLayout(source.Style);
        if ((long)layout.Width * layout.Height > Math.Max(0, remainingPixels)) return null;
        remainingPixels -= (long)layout.Width * layout.Height;
        var pixels = layout.Render();
        var layer = Layer.Raster(name, pixels, record.SourceLeft - TextLayout.Padding, record.SourceTop - TextLayout.Padding);
        layer.Text = source.Style;
        return layer;
    }

    private static double Pixels(double value, string unit, double resolution) => unit switch
    {
        "points" => value * resolution / 72,
        "inches" => value * resolution,
        "millimeters" => value / 25.4 * resolution,
        _ => value
    };

    private static readonly string[] StyleWords = ["Bold", "Semi-Bold", "SemiBold", "Demi-Bold", "DemiBold", "Extra-Bold", "ExtraBold", "Ultra-Bold", "Heavy", "Black", "Italic", "Oblique", "Light", "Extra-Light", "Thin", "Medium", "Regular", "Book", "Normal", "Condensed", "Expanded", "Narrow"];

    /// <summary>A Pango description, "Family Style Words", split into the family and whether it is bold or italic, with the family matched to an installed one when there is.</summary>
    internal static (string Family, bool Bold, bool Italic) Face(string description)
    {
        var words = description.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var styles = new List<string>();
        while (words.Count > 1 && StyleWords.Contains(words[^1], StringComparer.OrdinalIgnoreCase)) { styles.Add(words[^1]); words.RemoveAt(words.Count - 1); }
        // A trailing size ("Sans 12") is Pango's, not ours.
        if (words.Count > 1 && double.TryParse(words[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out _)) words.RemoveAt(words.Count - 1);
        var family = string.Join(' ', words);
        if (family.Length == 0) family = new TextStyle().FontFamily;
        var bold = styles.Any(s => s.Contains("Bold", StringComparison.OrdinalIgnoreCase) || s.Equals("Heavy", StringComparison.OrdinalIgnoreCase) || s.Equals("Black", StringComparison.OrdinalIgnoreCase));
        var italic = styles.Any(s => s.Equals("Italic", StringComparison.OrdinalIgnoreCase) || s.Equals("Oblique", StringComparison.OrdinalIgnoreCase));
        var installed = EditorSession.FontFamilies.FirstOrDefault(f => f.Equals(family, StringComparison.OrdinalIgnoreCase));
        if (installed == null && family is "Sans" or "Sans-serif") installed = EditorSession.FontFamilies.FirstOrDefault(f => f is "DejaVu Sans" or "Noto Sans" or "Liberation Sans" or "Arial" or "Inter");
        if (installed == null && family == "Serif") installed = EditorSession.FontFamilies.FirstOrDefault(f => f is "DejaVu Serif" or "Noto Serif" or "Liberation Serif" or "Times New Roman");
        if (installed == null && family == "Monospace") installed = EditorSession.FontFamilies.FirstOrDefault(f => f is "DejaVu Sans Mono" or "Noto Sans Mono" or "Liberation Mono" or "Consolas");
        return ((installed ?? family)[..Math.Min(TextStyle.MaxFamilyName, (installed ?? family).Length)], bold, italic);
    }

    /// <summary>
    /// GIMP 2.10 names the font in one string, "DejaVu Sans Bold"; GIMP 3 wraps a <c>GimpFont</c> form around a
    /// <c>fullname</c>, a <c>family</c> and a <c>style</c>, whose fullname is that same string.
    /// </summary>
    private static string? FontName(List<object> list)
    {
        if (list.Count > 1 && list[1] is string name && name != "GimpFont") return name;
        string? fullname = null, family = null, style = null;
        foreach (var item in list.Skip(1))
        {
            if (item is not List<object> { Count: > 1 } inner || inner[0] is not Symbol head || inner[1] is not string value) continue;
            if (head.Name == "fullname") fullname = value;
            else if (head.Name == "family") family = value;
            else if (head.Name == "style") style = value;
        }
        if (fullname != null) return fullname;
        if (family != null) return style != null && style != "Regular" ? $"{family} {style}" : family;
        return FirstString(list);
    }

    private static string? FirstString(List<object> list)
    {
        foreach (var item in list.Skip(1))
        {
            if (item is string s) return s;
            if (item is List<object> inner && FirstString(inner) is { } found) return found;
        }
        return null;
    }

    /// <summary>
    /// A color form. GIMP 2.10 writes <c>(color-rgba r g b a)</c> with the channels as 0 to 1. GIMP 3 writes a GEGL
    /// color: <c>(color "R'G'B'A float" 16 "…" 0)</c>, a pixel format, its byte count and the bytes as an escaped
    /// string, little-endian as the machine that saved it; primes mark gamma-encoded channels, their absence linear
    /// ones, which are brought through the sRGB curve here.
    /// </summary>
    private static uint? Color(List<object> list)
    {
        foreach (var item in list.Skip(1))
        {
            if (item is not List<object> inner || inner.Count < 4 || inner[0] is not Symbol head) continue;
            if (head.Name.StartsWith("color-rgb", StringComparison.Ordinal))
            {
                var channels = inner.Skip(1).Select(Number).Select(v => (byte)Math.Clamp(Math.Round(v * 255), 0, 255)).ToList();
                return (uint)new SKColor(channels[0], channels[1], channels[2], channels.Count > 3 ? channels[3] : (byte)255);
            }
            if (head.Name == "color" && inner[1] is string format && inner[3] is string encoded) return GeglColor(format, encoded);
            if (Color(inner) is { } nested) return nested;
        }
        return null;
    }

    private static uint? GeglColor(string format, string encoded)
    {
        var words = format.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2) return null;
        var layout = words[0];
        var gamma = layout.Contains('\'');
        var channels = layout.Count(c => c is 'R' or 'G' or 'B' or 'A' or 'Y');
        var bytes = encoded.Select(c => (byte)c).ToArray();
        var width = words[^1] switch { "float" => 4, "double" => 8, "u16" or "half" => 2, "u8" => 1, _ => 0 };
        if (channels == 0 || width == 0 || bytes.Length < channels * width) return null;
        var values = new double[channels];
        for (var i = 0; i < channels; i++)
        {
            var at = i * width;
            values[i] = words[^1] switch
            {
                "float" => BitConverter.ToSingle(bytes, at),
                "double" => BitConverter.ToDouble(bytes, at),
                "half" => (double)BitConverter.ToHalf(bytes, at),
                "u16" => BitConverter.ToUInt16(bytes, at) / 65535.0,
                _ => bytes[at] / 255.0
            };
        }
        var gray = layout.StartsWith('Y');
        double r = values[0], g = gray ? values[0] : values[1], b = gray ? values[0] : values[2];
        var a = layout.Contains('A') ? values[^1] : 1;
        byte Encode(double v) { v = Math.Clamp(double.IsFinite(v) ? v : 0, 0, 1); if (!gamma) v = Filters.CameraRawPixels.LinearToSrgb(v); return (byte)Math.Round(v * 255); }
        return (uint)new SKColor(Encode(r), Encode(g), Encode(b), (byte)Math.Round(Math.Clamp(a, 0, 1) * 255));
    }

    private static double Number(object? value) => value is double d && double.IsFinite(d) ? d : 0;

    internal sealed record Symbol(string Name);

    /// <summary>The parasite's notation: parenthesised forms of symbols, numbers, quoted strings with backslash escapes, and nested forms.</summary>
    internal static class Expressions
    {
        public static List<object> Parse(string text)
        {
            var at = 0;
            var forms = new List<object>();
            while (true)
            {
                SkipSpace(text, ref at);
                if (at >= text.Length) return forms;
                forms.Add(Read(text, ref at, 0));
            }
        }

        private static object Read(string text, ref int at, int depth)
        {
            if (depth > 64) throw new FormatException("Nested too deep.");
            SkipSpace(text, ref at);
            if (at >= text.Length) throw new FormatException("Ended early.");
            var c = text[at];
            if (c == '(')
            {
                at++;
                var list = new List<object>();
                while (true)
                {
                    SkipSpace(text, ref at);
                    if (at >= text.Length) throw new FormatException("A form never closed.");
                    if (text[at] == ')') { at++; return list; }
                    list.Add(Read(text, ref at, depth + 1));
                }
            }
            if (c == '"')
            {
                at++;
                var builder = new StringBuilder();
                while (at < text.Length && text[at] != '"')
                {
                    if (text[at] == '\\' && at + 1 < text.Length)
                    {
                        at++;
                        if (text[at] is >= '0' and <= '7')
                        {
                            // An octal escape of up to three digits: a raw byte, kept as a character of that value.
                            var value = 0;
                            var digits = 0;
                            while (digits < 3 && at < text.Length && text[at] is >= '0' and <= '7') { value = value * 8 + (text[at] - '0'); at++; digits++; }
                            builder.Append((char)(value & 0xFF));
                            continue;
                        }
                        builder.Append(text[at] switch { 'n' => '\n', 't' => '\t', var e => e });
                    }
                    else builder.Append(text[at]);
                    at++;
                }
                if (at >= text.Length) throw new FormatException("A string never closed.");
                at++;
                return builder.ToString();
            }
            var start = at;
            while (at < text.Length && !char.IsWhiteSpace(text[at]) && text[at] != '(' && text[at] != ')') at++;
            var token = text[start..at];
            if (token.Length == 0) throw new FormatException("Unexpected character.");
            return double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : new Symbol(token);
        }

        private static void SkipSpace(string text, ref int at)
        {
            while (at < text.Length)
            {
                if (char.IsWhiteSpace(text[at])) at++;
                else if (text[at] == ';') { while (at < text.Length && text[at] != '\n') at++; }
                else break;
            }
        }
    }
}
