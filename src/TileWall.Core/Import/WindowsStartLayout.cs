using System.Globalization;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace TileWall.Core.Import;

internal enum WindowsStartPinKind { Unresolved, LinkPath, ApplicationId }

internal sealed record WindowsStartPin(WindowsStartPinKind Kind, string Value);
internal sealed record WindowsStartPinnedGroup(string Title, IReadOnlyList<WindowsStartPin> Pins);

/// <summary>Ordered identity facts from one Windows Start layout export.</summary>
internal sealed class WindowsStartLayout(IReadOnlyList<WindowsStartPinnedGroup> groups)
{
    internal const int MaximumBytes = 4 * 1024 * 1024;
    private const int MaximumPins = 50000;
    private static readonly XNamespace Modification = "http://schemas.microsoft.com/Start/2014/LayoutModification";
    private static readonly XNamespace DefaultLayout = "http://schemas.microsoft.com/Start/2014/FullDefaultLayout";
    private static readonly XNamespace Start = "http://schemas.microsoft.com/Start/2014/StartLayout";

    internal IReadOnlyList<WindowsStartPinnedGroup> Groups { get; } = groups;
    internal int TotalCount { get; } = groups.Sum(group => group.Pins.Count);

    internal static WindowsStartLayout Parse(byte[] bytes, bool json, string defaultGroupName, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (bytes.Length is < 1 or > MaximumBytes) throw new InvalidDataException("Invalid Start layout size.");
        try
        {
            var layout = json ? ParseJson(bytes, defaultGroupName, token) : ParseXml(bytes, token);
            if (layout.TotalCount > MaximumPins) throw new InvalidDataException("The Start layout has too many fixed items.");
            return layout;
        }
        catch (Exception error) when (error is XmlException or JsonException)
        {
            throw new InvalidDataException("The Windows Start layout could not be parsed.", error);
        }
    }

    private static WindowsStartLayout ParseXml(byte[] bytes, CancellationToken token)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumBytes,
        });
        var root = XDocument.Load(reader).Root;
        if (root is null || root.Name != Modification + "LayoutModificationTemplate" || (string?)root.Attribute("Version") != "1")
            throw new InvalidDataException("The XML Start layout root is invalid.");
        var layoutOverride = RequireChild(root, Modification + "DefaultLayoutOverride");
        var collection = RequireChild(layoutOverride, Modification + "StartLayoutCollection");
        var groups = new List<WindowsStartPinnedGroup>();
        foreach (var layout in collection.Elements())
        {
            if (layout.Name != DefaultLayout + "StartLayout") throw new InvalidDataException("Unknown XML Start layout.");
            foreach (var group in layout.Elements())
            {
                token.ThrowIfCancellationRequested();
                if (group.Name != Start + "Group") throw new InvalidDataException("Unknown XML Start group.");
                var pins = new List<(int Row, int Column, WindowsStartPin Pin)>();
                foreach (var element in group.Elements())
                {
                    token.ThrowIfCancellationRequested();
                    pins.Add((Coordinate(element, "Row"), Coordinate(element, "Column"), ReadXmlPin(element)));
                }
                // Export order is not reading order. Stable sorting retains the order of equal positions.
                groups.Add(new((string?)group.Attribute("Name") ?? "", Array.AsReadOnly(pins
                    .OrderBy(pin => pin.Row).ThenBy(pin => pin.Column).Select(pin => pin.Pin).ToArray())));
            }
        }
        return new(groups.AsReadOnly());
    }

    private static XElement RequireChild(XElement parent, XName name)
    {
        var matches = parent.Elements(name).Take(2).ToArray();
        if (matches.Length != 1) throw new InvalidDataException($"Expected one {name.LocalName} in the XML Start layout.");
        return matches[0];
    }

    private static int Coordinate(XElement element, string name) =>
        int.TryParse((string?)element.Attribute(name), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value : throw new InvalidDataException($"Invalid {name} in the XML Start layout.");

    private static WindowsStartPin ReadXmlPin(XElement pin)
    {
        if (pin.Name == Start + "DesktopApplicationTile")
        {
            if ((string?)pin.Attribute("DesktopApplicationLinkPath") is { } path)
                return new(WindowsStartPinKind.LinkPath, path);
            if ((string?)pin.Attribute("DesktopApplicationID") is { } desktopId)
                return new(WindowsStartPinKind.ApplicationId, desktopId);
        }
        if (pin.Name == Start + "Tile" && (string?)pin.Attribute("AppUserModelID") is { } appId)
            return new(WindowsStartPinKind.ApplicationId, appId);
        return new(WindowsStartPinKind.Unresolved, pin.ToString(SaveOptions.DisableFormatting));
    }

    private static WindowsStartLayout ParseJson(byte[] bytes, string defaultGroupName, CancellationToken token)
    {
        ReadOnlyMemory<byte> json = bytes;
        if (json.Span.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) json = json[3..];
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowDuplicateProperties = false });
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("pinnedList", out var list) || list.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The JSON Start layout pinned list is invalid.");
        var pins = new List<WindowsStartPin>();
        foreach (var item in list.EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            pins.Add(ReadJsonPin(item));
        }
        return new(pins.Count == 0 ? [] : [new(defaultGroupName, pins.AsReadOnly())]);
    }

    private static WindowsStartPin ReadJsonPin(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("An item in the JSON Start layout is invalid.");
        WindowsStartPin? pin = null;
        foreach (var property in item.EnumerateObject())
        {
            var kind = property.Name switch
            {
                "desktopAppLink" => WindowsStartPinKind.LinkPath,
                "packagedAppId" or "desktopAppId" => WindowsStartPinKind.ApplicationId,
                _ => WindowsStartPinKind.Unresolved,
            };
            if (kind == WindowsStartPinKind.Unresolved) continue;
            if (pin is not null || property.Value.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("An identity in the JSON Start layout is invalid.");
            pin = new(kind, property.Value.GetString()!);
        }
        return pin ?? new(WindowsStartPinKind.Unresolved, item.GetRawText());
    }
}
