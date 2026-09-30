using piratechess_lib;

namespace PirateChess.Api.Services;

/// <summary>Ergebnis von <see cref="CourseDataProvider.GetOrFetchAsync"/>: Kursdaten, oder kein Bearer beim
/// Cache-Miss, oder die (bereinigte) Fehlermeldung des Chessable-Abrufs.</summary>
public readonly record struct CourseDataResult(RestResponseCourse? Data, bool BearerMissing, string? FetchError);

/// <summary>
/// Rohdaten eines Kurses aus dem (bid-weiten) Cache oder frisch von Chessable (S2-015): Cache lesen → Per-bid-Lock
/// → Double-Check → Abruf → Cache schreiben. Eine Stelle für den synchronen course-Endpoint und den course/start-Job.
/// </summary>
public class CourseDataProvider
{
    private readonly IChessableHttpService _chessableHttp;
    private readonly RawCourseCache _rawCache;

    public CourseDataProvider(IChessableHttpService chessableHttp, RawCourseCache rawCache)
    {
        _chessableHttp = chessableHttp;
        _rawCache = rawCache;
    }

    /// <param name="bearer">Nur für den echten Chessable-Abruf (Cache-Miss) nötig — ein gecachter Kurs wird auch
    /// ohne bedient.</param>
    /// <param name="forceRefresh">Gecachte Rohdaten überspringen → echter Neu-Abruf, auch der Linien.</param>
    /// <param name="preloaded">Schon geladene Rohdaten (course/start-Gate), damit der Kurs nicht zweimal geladen wird.</param>
    public async Task<CourseDataResult> GetOrFetchAsync(
        string bid, string bearer, string uid, bool forceRefresh,
        RestResponseCourse? preloaded = null,
        Action<string>? onChapterProgress = null,
        Action<string>? onCumulativeLines = null,
        Action<int>? onTotalLines = null,
        CancellationToken ct = default)
    {
        // Rohdaten aus dem (kurs-/bid-weiten) Cache wiederverwenden → kein Chessable-Call,
        // auch wenn ein anderer User denselben Kurs schon geholt hat.
        var data = forceRefresh ? null : preloaded ?? await _rawCache.GetAsync(bid, ct);
        if (data is not null)
            return new CourseDataResult(data, false, null);
        if (string.IsNullOrWhiteSpace(bearer))
            return new CourseDataResult(null, true, null);

        // Per-Bid-Lock: zwei parallele Cache-Misses desselben Kurses sollen nicht beide über
        // die VPN-IP fetchen. Nach Lock-Eintritt erneut prüfen (ein paralleler Fetch könnte den
        // Cache inzwischen gefüllt haben). Dispose räumt den Lock-Eintrag per Refcount wieder ab.
        using (await _rawCache.AcquireBidLockAsync(bid, ct))
        {
            // Force-Refresh UMGEHT den Cache, statt ihn vorher zu löschen: `CachedRawLines` ist
            // der einzige dauerhafte Linien-Speicher (das Audit hat nur 14 Tage Retention).
            // Vorab-Löschen hieße: scheitert der Abruf (Chessable-Block, totes Bearer), ist ein
            // vorher funktionierender Kurs unwiederbringlich weg. Bei Erfolg überschreibt der
            // Upsert die alten Einträge ohnehin.
            data = forceRefresh ? null : await _rawCache.GetAsync(bid, ct); // Double-Check: paralleler Fetch evtl. fertig
            if (data is not null)
                return new CourseDataResult(data, false, null);

            var (fetched, fetchError) = await _chessableHttp.FetchCourseDataAsync(bearer, uid, bid,
                onChapterProgress: onChapterProgress,
                onCumulativeLines: onCumulativeLines,
                onTotalLines: onTotalLines,
                bypassLineCache: forceRefresh,
                ct: ct);
            // Der Kursabruf meldet einen Abbruch teils als Fehlertext statt als Ausnahme.
            ct.ThrowIfCancellationRequested();
            if (fetchError is not null)
                return new CourseDataResult(null, false, fetchError.Trim() is "{}" or "" ? "Invalid bearer" : fetchError);

            if (fetched is not null) await _rawCache.SetAsync(bid, fetched, ct);
            return new CourseDataResult(fetched, false, null);
        }
    }
}
