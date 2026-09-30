using PirateChess.Api.Models.DTOs;

namespace PirateChess.Api.Services;

/// <summary>
/// Browser-Import (course/parse, S2-015 aus dem Controller gelöst): setzt das vom Browser erfasste rohe Chessable-JSON
/// zu einem Kurs zusammen (fehlende Linien aus dem geteilten Linien-Cache), erzeugt das PGN und legt den Upload im
/// geteilten Cache ab. Kein Chessable-Kontakt, kein Bearer.
/// </summary>
public class BrowserCourseParseService
{
    private readonly RawCourseCache _rawCache;
    private readonly RawLineCache _lineCache;
    private readonly IConfiguration _config;
    private readonly ILogger<BrowserCourseParseService> _logger;

    public BrowserCourseParseService(RawCourseCache rawCache, RawLineCache lineCache, IConfiguration config,
        ILogger<BrowserCourseParseService> logger)
    {
        _rawCache = rawCache;
        _lineCache = lineCache;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Darf ein vollständiger Browser-Upload den Kurs-Cache eines bids anlegen? Standardmäßig NEIN (A3-002): die
    /// Kapitelzahl stammt aus dem courseJson des Clients, piratechess kann sie ohne eigenen getCourse-Abruf nicht
    /// prüfen — ein selbstkonsistenter Upload mit einem Kapitel legte sonst den Kurs-Cache des GANZEN bids an, den
    /// danach jeder Server-Import (Fast-Lane, Admin „Kurse von Usern holen", Reprocess) für alle liefert.
    /// Einschalten mit <c>Chessable:BrowserCourseCacheEnabled=true</c> ist eine Produktentscheidung.
    /// </summary>
    private bool BrowserCourseCacheEnabled => _config.GetValue("Chessable:BrowserCourseCacheEnabled", false);

    /// <summary>
    /// Parst einen geprüften Browser-Upload (bid, Modus, mindestens ein Kapitel in gültiger Form) zu PGN. Liefert
    /// entweder die Antwort oder die Fehlermeldung für ein 400.
    /// </summary>
    public async Task<(DirectCourseResponse? Response, string? Error)> ParseAsync(DirectCourseParseRequest request,
        List<DirectParseChapter> chapters, TrainingMode mode, CancellationToken ct)
    {
        // Das getCourse-JSON dient im Local-Mode nur der Kapitel-ITERATION (die id/lid ist dort ungenutzt,
        // Kapitel/Linien werden rein positionsbasiert gelesen). Daher aus der Kapitelanzahl synthetisieren,
        // statt auf ein separat erfasstes clientseitiges getCourse zu vertrauen (Anzahl-Mismatch vermieden).
        var courseJson = "{\"course\":{\"data\":[" +
            string.Join(",", Enumerable.Range(0, chapters.Count).Select(i => $"{{\"id\":{i}}}")) +
            "]}}";

        // Linien ohne Inhalt hat der Browser bewusst nicht bei Chessable geholt, weil sie im geteilten
        // Linien-Cache liegen → in EINER gebatchten Abfrage nachladen, nur Linien DIESES Kurses (oder Altbestand).
        var fillOids = BrowserCourseAssembler.OidsToFill(chapters);
        var cachedLines = fillOids.Count > 0
            ? await _lineCache.GetManyAsync(fillOids, request.Bid, ct)
            : new Dictionary<int, string>();

        var data = new piratechess_lib.RestResponseCourse { CourseJsonContent = courseJson };
        int linesFromCache = 0, linesMissing = 0;
        var allAligned = true;
        foreach (var ch in chapters)
        {
            if (ch.LineOids is null)
            {
                // Alt-Client ohne oids: positionsbasiert wie bisher.
                allAligned = false;
                var rc = new piratechess_lib.RestResponseChapter { ChapterJsonContent = ch.ChapterJson };
                foreach (var ln in ch.Lines ?? [])
                    rc.ResponseLineList.Add(new piratechess_lib.RestResponseLine { LineJsonContent = ln });
                data.ChapterList.Add(rc);
                continue;
            }
            var aligned = BrowserCourseAssembler.Align(ch, cachedLines);
            data.ChapterList.Add(aligned.Chapter);
            linesFromCache += aligned.FromCache;
            linesMissing += aligned.Missing;
        }

        var lib = new piratechess_lib.PirateChessLib { restResponseCourse = data };
        mode.ApplyTo(lib);

        lib.SetErrorDiagEvent(detail =>
            _logger.LogWarning("Chessable-Parser übersprang eine Linie/Kapitel beim Browser-Parse (bid {Bid}): {Detail}", request.Bid, detail));

        string pgn, courseName;
        try
        {
            (pgn, courseName) = await Task.Run(() => lib.GetCourse(request.Bid, useLocalData: true), ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Browser-Parse PGN generation failed for bid {Bid}", request.Bid);
            return (null, $"PGN generation failed: {ex.Message}");
        }
        if (lib.ErrorCount > 0)
            _logger.LogWarning("Browser-Parse bid {Bid} mit {Errors} übersprungenen Linien/Kapiteln", request.Bid, lib.ErrorCount);

        if (linesFromCache > 0 || linesMissing > 0)
            _logger.LogInformation("Browser-Parse bid {Bid}: {FromCache} Linien aus dem geteilten Cache, {Missing} ohne Inhalt ausgelassen",
                request.Bid, linesFromCache, linesMissing);
        try
        {
            await StoreInSharedCacheAsync(request, chapters, data, allAligned, linesMissing, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Der Import ist schon geparst; ein Fehler beim Ablegen im geteilten Cache kostet nur den Cache-Eintrag.
            _logger.LogWarning(ex, "Browser-Parse bid {Bid}: Ablegen im geteilten Cache fehlgeschlagen — Import geht ohne Cache weiter", request.Bid);
        }

        var lineCount = data.ChapterList.Sum(c => c.ResponseLineList.Count);
        return (new DirectCourseResponse(request.Bid, courseName, mode.ToString(), data.ChapterList.Count, lineCount, pgn), null);
    }

    /// <summary>
    /// Legt einen Browser-Upload im geteilten Rohdaten-Cache ab, damit spätere Importe — anderer Nutzer wie
    /// des Servers — diese Linien nicht erneut bei Chessable holen:
    /// (1) jede mitgeschickte Linie mit oid, deren getGame-Antwort selbst dieselbe oid und diesen Kurs nennt
    ///     (<see cref="BrowserCourseAssembler.SharableLines"/>), sofern es für die oid noch keine Zeile gibt (nie
    ///     überschreiben, auch keine als ungültig markierte);
    /// (2) den ganzen Kurs NUR mit <c>Chessable:BrowserCourseCacheEnabled=true</c> (Standard: aus) und nur, wenn der
    ///     Browser ihn als vollständig meldet: <c>Complete</c>, ein <c>CourseJson</c> mit derselben Kapitelzahl, keine
    ///     Linie ohne Inhalt, jede Linie danach gültig unter diesem Kurs im Linien-Cache, noch kein Eintrag vorhanden.
    /// Ein Teil-Kurs im Kurs-Cache gälte für ALLE als „vollständig gecacht" und würde nie mehr frisch geholt.
    /// BEKANNT OFFEN (A3-002), darum der Schalter: <c>Complete</c> und <c>CourseJson</c> sind Client-Angaben, KEINE
    /// echte getCourse-Antwort — piratechess prüft die Kapitelzahl nur gegen sich selbst. Und die Prüfung „jede Linie
    /// gültig unter diesem Kurs" zählt die Linien mit, die (1) im SELBEN Aufruf gerade abgelegt hat. Ein Chunk mit
    /// einem Kapitel + ein erfundenes courseJson mit einem Kapitel + selbstkonsistente (oder echte) Linien legt so
    /// den Kurs-Cache des ganzen bids an.
    /// Nie fatal — der Import ist zu diesem Zeitpunkt schon geparst (der Aufrufer fängt jeden Fehler ab).
    /// </summary>
    private async Task StoreInSharedCacheAsync(DirectCourseParseRequest request, List<DirectParseChapter> chapters,
        piratechess_lib.RestResponseCourse parsed, bool allAligned, int linesMissing, CancellationToken ct)
    {
        var provided = BrowserCourseAssembler.ProvidedLines(chapters);
        var sharable = BrowserCourseAssembler.SharableLines(provided, request.Bid);
        if (sharable.Count < provided.Count)
            _logger.LogInformation("Browser-Parse bid {Bid}: {Count} Linien nennen nicht selbst dieselbe oid und diesen Kurs — nicht geteilt",
                request.Bid, provided.Count - sharable.Count);
        var added = await _lineCache.AddMissingAsync(sharable, request.Bid, ct);
        if (added > 0)
            _logger.LogInformation("Browser-Parse bid {Bid}: {Added} Linien neu im geteilten Cache", request.Bid, added);

        if (!request.Complete || !allAligned || linesMissing > 0 || string.IsNullOrWhiteSpace(request.CourseJson))
            return;
        if (!BrowserCourseCacheEnabled)
        {
            _logger.LogInformation("Browser-Parse bid {Bid}: als vollständig gemeldet, Kurs-Cache aus dem Browser ist aus (Chessable:BrowserCourseCacheEnabled) — nur Linien-Cache", request.Bid);
            return;
        }
        // Client-Angabe gegen Client-Angabe: fängt nur eine inkonsistente Meldung ab, keine erfundene.
        if (BrowserCourseAssembler.CourseChapterCount(request.CourseJson) != parsed.ChapterList.Count)
        {
            _logger.LogInformation("Browser-Parse bid {Bid}: Kapitelzahl weicht vom mitgeschickten courseJson ab — kein Kurs-Cache", request.Bid);
            return;
        }
        // Der Kurs-Cache verweist nur auf oids, die Inhalte liest er aus dem Linien-Cache. Liegt nicht JEDE Linie
        // gültig unter diesem Kurs dort (nicht geteilt, markiert, einem anderen Kurs zugeordnet), bleibt es beim
        // Linien-Cache — sonst schriebe das Seeding des Kurs-Caches den Client-Inhalt doch noch hinein. Mitgezählt
        // werden auch die eben in (1) als FromBrowser abgelegten Linien: das hält fremde/markierte Linien fern,
        // bestätigt aber keinen Inhalt und keine Kapitelzahl (s. Schalter oben).
        var courseOids = parsed.ChapterList.SelectMany(c => c.ResponseLineList).Select(l => l.Oid).Distinct().ToList();
        var shared = await _lineCache.GetCachedOidsAsync(courseOids, request.Bid, ct);
        if (shared.Count < courseOids.Count)
        {
            _logger.LogInformation("Browser-Parse bid {Bid}: {Count} Linien nicht gültig unter diesem Kurs im Linien-Cache — kein Kurs-Cache",
                request.Bid, courseOids.Count - shared.Count);
            return;
        }

        // Derselbe Per-bid-Lock wie der Server-Abruf, aber nur kurz warten: hält ihn gerade ein laufender
        // Abruf, schreibt der ohnehin gleich den Kurs — dann verzichtet der Browser-Upload.
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(TimeSpan.FromSeconds(2));
        IDisposable bidLock;
        try
        {
            bidLock = await _rawCache.AcquireBidLockAsync(request.Bid, wait.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogInformation("Browser-Parse bid {Bid}: Kurs gerade im Server-Abruf — kein Kurs-Cache aus dem Browser", request.Bid);
            return;
        }
        using (bidLock)
        {
            if (await _rawCache.HasEntryAsync(request.Bid, ct))
                return;
            await _rawCache.SetAsync(request.Bid, new piratechess_lib.RestResponseCourse
            {
                CourseJsonContent = request.CourseJson,
                ChapterList = parsed.ChapterList,
            }, ct);
        }
    }
}
