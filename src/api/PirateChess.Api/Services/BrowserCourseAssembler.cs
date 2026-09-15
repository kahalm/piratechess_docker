using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using piratechess_lib;
using PirateChess.Api.Models.DTOs;

namespace PirateChess.Api.Services;

/// <summary>
/// Setzt die vom Browser erfassten Kapitel für den fetch-freien Parser zusammen.
///
/// Der Parser (piratechess_lib, Local-Mode) liest POSITIONSBASIERT: der n-te Eintrag von <c>list.data</c>
/// bekommt die n-te Linie. Schickt der Browser nur einen Teil der Linien eines Kapitels (inkrementelles
/// „Kurs holen", Live-Anhängen, Mitschnitt), rutschte jede Linie auf den falschen Eintrag und landete unter
/// fremder oid und fremdem Namen. Mit <c>LineOids</c> ordnet <see cref="Align"/> jede Linie ihrem Eintrag zu,
/// füllt fehlende aus dem geteilten Linien-Cache und nimmt Einträge ohne Inhalt aus <c>list.data</c>, damit
/// der Parser nicht verrutscht.
/// </summary>
public static class BrowserCourseAssembler
{
    /// <summary>Obergrenze einer Cache-Abfrage — hält den Body unter dem 256-KB-Limit von direct/*.</summary>
    public const int MaxOidsPerLookup = 10000;

    public sealed record AlignedChapter(RestResponseChapter Chapter, int Provided, int FromCache, int Missing);

    public static bool TryParseOid(string? raw, out int oid)
        => int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out oid) && oid > 0;

    /// <summary>Prüft die Form der Kapitel. <c>null</c> = in Ordnung, sonst die Fehlermeldung.</summary>
    public static string? Validate(IReadOnlyList<DirectParseChapter> chapters)
    {
        foreach (var ch in chapters)
        {
            var lines = ch.Lines ?? [];
            if (ch.LineOids is null)
            {
                // Ohne oid gibt es nichts, womit eine Linie ohne Inhalt aus dem Cache gefüllt werden könnte.
                if (lines.Any(string.IsNullOrEmpty))
                    return "A line without content needs lineOids (it is filled from the shared cache).";
                continue;
            }
            if (ch.LineOids.Count != lines.Count)
                return "lineOids must have exactly one entry per line.";
            if (ch.LineOids.Any(o => !TryParseOid(o, out _)))
                return "Invalid oid in lineOids.";
        }
        return null;
    }

    /// <summary>oids, deren Inhalt aus dem geteilten Cache kommen soll: Linie ohne Inhalt, aber mit oid.</summary>
    public static HashSet<int> OidsToFill(IEnumerable<DirectParseChapter> chapters)
    {
        var result = new HashSet<int>();
        foreach (var ch in chapters)
        {
            if (ch.LineOids is null || ch.Lines is null) continue;
            for (var i = 0; i < ch.Lines.Count && i < ch.LineOids.Count; i++)
                if (string.IsNullOrEmpty(ch.Lines[i]) && TryParseOid(ch.LineOids[i], out var oid))
                    result.Add(oid);
        }
        return result;
    }

    /// <summary>Die vom Browser mitgeschickten Linien mit oid (Inhalt vorhanden).</summary>
    public static Dictionary<int, string> ProvidedLines(IEnumerable<DirectParseChapter> chapters)
    {
        var result = new Dictionary<int, string>();
        foreach (var ch in chapters)
        {
            if (ch.LineOids is null || ch.Lines is null) continue;
            for (var i = 0; i < ch.Lines.Count && i < ch.LineOids.Count; i++)
                if (!string.IsNullOrEmpty(ch.Lines[i]) && TryParseOid(ch.LineOids[i], out var oid))
                    result[oid] = ch.Lines[i]!;
        }
        return result;
    }

    /// <summary>
    /// Ordnet die Linien eines Kapitels über ihre oid der Reihenfolge von <c>list.data</c> zu. Vorrang hat
    /// die mitgeschickte Linie, sonst die gecachte; Einträge ohne beides fallen aus <c>list.data</c> heraus.
    /// Fehlt nichts, bleibt das Kapitel-JSON Byte für Byte erhalten.
    /// </summary>
    public static AlignedChapter Align(DirectParseChapter chapter, IReadOnlyDictionary<int, string> cachedLines)
    {
        var provided = ProvidedLines([chapter]);
        var rawJson = chapter.ChapterJson ?? string.Empty;

        JsonNode? root = null;
        try { root = JsonNode.Parse(rawJson); } catch (JsonException) { }
        var (listObj, dataName, data) = FindData(root);
        if (listObj is null || dataName is null || data is null)
        {
            // Unbrauchbares Kapitel-JSON: unverändert weiterreichen, der Parser überspringt das Kapitel
            // und zählt es als Fehler — genauso wie ohne oids.
            var passthrough = new RestResponseChapter { ChapterJsonContent = rawJson };
            foreach (var (oid, content) in provided)
                passthrough.ResponseLineList.Add(new RestResponseLine { Oid = oid, LineJsonContent = content });
            return new AlignedChapter(passthrough, provided.Count, 0, 0);
        }

        var kept = new JsonArray();
        var lines = new List<RestResponseLine>();
        int fromProvided = 0, fromCache = 0, missing = 0;
        foreach (var entry in data)
        {
            string? content = null;
            var id = ReadId(entry);
            if (id is int oid)
            {
                if (provided.TryGetValue(oid, out var p)) { content = p; fromProvided++; }
                else if (cachedLines.TryGetValue(oid, out var c)) { content = c; fromCache++; }
            }
            if (content is null || id is null) { missing++; continue; }
            kept.Add(entry?.DeepClone());
            lines.Add(new RestResponseLine { Oid = id.Value, LineJsonContent = content });
        }

        string chapterJson;
        if (missing == 0)
            chapterJson = rawJson;
        else
        {
            listObj[dataName] = kept;
            chapterJson = root!.ToJsonString();
        }

        var result = new RestResponseChapter { ChapterJsonContent = chapterJson };
        result.ResponseLineList.AddRange(lines);
        return new AlignedChapter(result, fromProvided, fromCache, missing);
    }

    /// <summary>
    /// Taugt der Inhalt als Eintrag im geteilten Cache? Er muss ein <c>game</c>-Objekt tragen: ein beliebiges
    /// JSON wie <c>{"x":1}</c> parst sonst zu einer leeren Linie und vergiftete den Cache für alle.
    /// </summary>
    public static bool IsCacheableLine(string? content) => RawLineCache.InvalidReason(content) is null;

    /// <summary>Trägt der (syntaktisch gültige) Inhalt ein <c>game</c>-Objekt?</summary>
    internal static bool HasGameObject(string content)
    {
        try
        {
            return JsonNode.Parse(content) is JsonObject obj && Property(obj, "game") is JsonObject;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>Kapitelzahl einer getCourse-Antwort, <c>null</c> wenn sie nicht lesbar ist.</summary>
    public static int? CourseChapterCount(string? courseJson)
    {
        try
        {
            if (JsonNode.Parse(courseJson ?? string.Empty) is not JsonObject root) return null;
            if (Property(root, "course") is not JsonObject course) return null;
            return Property(course, "data") is JsonArray data ? data.Count : null;
        }
        catch (JsonException) { return null; }
    }

    private static (JsonObject? ListObj, string? DataName, JsonArray? Data) FindData(JsonNode? root)
    {
        if (root is not JsonObject rootObj || Property(rootObj, "list") is not JsonObject listObj)
            return (null, null, null);
        var dataName = listObj.Select(kv => kv.Key)
            .FirstOrDefault(k => string.Equals(k, "data", StringComparison.OrdinalIgnoreCase));
        return dataName is not null && listObj[dataName] is JsonArray arr ? (listObj, dataName, arr) : (null, null, null);
    }

    private static JsonNode? Property(JsonObject obj, string name)
        => obj.FirstOrDefault(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static int? ReadId(JsonNode? entry)
    {
        if (entry is not JsonObject obj || Property(obj, "id") is not JsonValue value) return null;
        if (value.TryGetValue<int>(out var i)) return i > 0 ? i : null;
        if (value.TryGetValue<long>(out var l)) return l > 0 && l <= int.MaxValue ? (int)l : null;
        if (value.TryGetValue<string>(out var s) && TryParseOid(s, out var parsed)) return parsed;
        return null;
    }
}
