using piratechess_lib;

namespace PirateChess.Api.Services;

/// <summary>
/// PGN-Schritt des course/start-Jobs (Zerlegung Z47; der synchrone Kursabruf ist seit I2-011 entfernt): Bibliothek mit den
/// Rohdaten anlegen, Trainingsmodus setzen, übersprungene Linien/Kapitel melden lassen; Zählungen der Rohdaten.
/// </summary>
public static class CoursePgn
{
    /// <param name="onParserSkipped">Erhält je übersprungener Linie/Kapitel die Diagnose der Bibliothek.</param>
    public static PirateChessLib CreateLib(RestResponseCourse? data, TrainingMode mode, Action<string> onParserSkipped)
    {
        var lib = new PirateChessLib { restResponseCourse = data };
        mode.ApplyTo(lib);
        lib.SetErrorDiagEvent(onParserSkipped);
        return lib;
    }

    public static int ChapterCount(RestResponseCourse? data) => data?.ChapterList.Count ?? 0;

    public static int LineCount(RestResponseCourse? data) => data?.ChapterList.Sum(c => c.ResponseLineList.Count) ?? 0;
}
