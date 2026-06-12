using System.Collections.Concurrent;
using piratechess_lib;

namespace PirateChess.Api.Services;

/// <summary>
/// In-Memory-Cache der rohen Kursstruktur (<see cref="RestResponseCourse"/>) je (uid, bid).
/// Damit ein zweiter Import desselben Kurses (z. B. erst als Repertoire, dann als Buch) das
/// PGN aus den schon geholten Rohdaten neu erzeugt, OHNE Chessable erneut abzurufen.
/// Geht bei einem Neustart verloren (dann wird wieder geholt) — das ist ok.
/// </summary>
public class RawCourseCache
{
    private const int MaxEntries = 10;
    private readonly ConcurrentDictionary<string, RestResponseCourse> _cache = new();

    private static string Key(string uid, string bid) => $"{uid}:{bid}";

    public RestResponseCourse? Get(string uid, string bid)
        => _cache.TryGetValue(Key(uid, bid), out var c) ? c : null;

    public void Set(string uid, string bid, RestResponseCourse course)
    {
        var key = Key(uid, bid);
        // Einfache Begrenzung: bei Überlauf ein paar alte Einträge entfernen (personal tool).
        if (!_cache.ContainsKey(key) && _cache.Count >= MaxEntries)
        {
            foreach (var k in _cache.Keys.Take(_cache.Count - MaxEntries + 1).ToList())
                _cache.TryRemove(k, out _);
        }
        _cache[key] = course;
    }
}
