using ChessDotNet;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace piratechess_lib
{
    /// <summary>
    /// Chessable-„V"-Daten eines Zuges → PGN-Varianten bzw. Kommentar (siehe <see cref="JsonMoveItemList.GetVariationParts"/>):
    /// Cluster bilden, jeden ab Eltern-, Hauptlinien- oder Varianten-Stellung eindeutig nachspielen, mehrdeutige
    /// Figurenzüge eindeutig schreiben. Aus <c>JsonMoveItemList</c> herausgelöst (Zerlegung Z48).
    /// </summary>
    internal static partial class VariationResolver
    {
        internal static List<(int Anchor, string Pgn)> GetParts(JsonMoveItemList entry, string branchFen, IReadOnlyList<string> anchorFens)
        {
            var result = new List<(int Anchor, string Pgn)>();
            if (entry.Key != "V" || entry.Val == null || entry.Val.Value.ValueKind != JsonValueKind.Array)
                return result;

            var innerList = JsonSerializer.Deserialize<List<JsonMoveItemList>>(entry.Val.Value, options: Options.GetOptions()) ?? [];

            // ---- Phase 2: jeden Cluster nachspielen → Variante, Unter-Variante oder Kommentar ----
            var rendered = new List<RenderedCluster>();   // schon gebaute Varianten DIESES V-Blocks
            var entries = new List<(int Anchor, RenderedCluster? Cluster, string Text)>();

            foreach (var cluster in SplitClusters(innerList))
            {
                var (moves, usedFen, anchor, host, hostSpot) = PlaceCluster(cluster, branchFen, anchorFens, rendered);
                RenderedCluster? built = moves != null ? BuildCluster(cluster, moves, usedFen!) : null;
                if (built == null)
                {
                    string t = PlainTextOf(cluster);
                    if (t != "") entries.Add((-1, null, $"{{{t}}}"));
                    continue;
                }

                rendered.Add(built);
                if (host != null) host.AddNested(hostSpot, built);   // wird MIT dem Gastgeber ausgegeben
                else entries.Add((anchor, built, ""));
            }

            foreach (var (a, c, t) in entries)
                result.Add((a, c != null ? c.ToPgn() : t));
            return result;
        }

        /// <summary>Phase 1: die Items an Zugnummern-Rücksprüngen in Cluster (einzelne Alternativlinien) zerlegen.</summary>
        private static List<List<JsonMoveItemList>> SplitClusters(List<JsonMoveItemList> innerList)
        {
            var clusters = new List<List<JsonMoveItemList>>();
            var cur = new List<JsonMoveItemList>();
            int lastOrder = int.MinValue;
            foreach (var item in innerList)
            {
                if (item.Key == "S")
                {
                    string raw = (item.Val?.ValueKind == JsonValueKind.String ? item.Val.Value.GetString() : "") ?? "";
                    int? ord = MoveOrder(raw);
                    if (ord.HasValue)
                    {
                        if (ord.Value <= lastOrder && cur.Count > 0)
                        {
                            clusters.Add(cur);
                            cur = [];
                            lastOrder = int.MinValue;
                        }
                        lastOrder = ord.Value;
                    }
                    else if (lastOrder != int.MinValue)
                    {
                        // Ein Zug OHNE Nummer setzt die Folge fort und belegt damit den nächsten Halbzug.
                        // Ohne dieses Mitzählen sah „… 3.Nc3 a6 … 3...h6" wie eine Fortsetzung aus (7 > 6),
                        // obwohl „a6" den Halbzug 7 schon belegt — der ganze Block wurde ein Kommentar.
                        lastOrder++;
                    }
                }
                cur.Add(item);
            }
            if (cur.Count > 0) clusters.Add(cur);
            return clusters;
        }

        /// <summary>Phase 2: wo ist der Cluster eindeutig spielbar — ab der Elternstellung, ab der Hauptlinien-Stellung
        /// seiner Zugnummer oder als Alternative innerhalb einer schon gebauten Variante (<paramref name="rendered"/>)?
        /// <c>Moves</c> null = nirgends (wird Kommentar); <c>Host</c> gesetzt = Unter-Variante dort.</summary>
        private static (List<string>? Moves, string? UsedFen, int Anchor, RenderedCluster? Host, int HostSpot) PlaceCluster(
            List<JsonMoveItemList> cluster, string branchFen, IReadOnlyList<string> anchorFens, List<RenderedCluster> rendered)
        {
            // Erst die Züge: eine Variante wird der Cluster nur, wenn JEDER Zug eindeutig spielbar ist.
            var raws = cluster.Where(it => it.Key == "S").Select(SanTextOf).Where(r => r != "").ToList();
            bool hasNull = raws.Any(r => r.Contains("--"));
            int? ord = raws.Count > 0 ? MoveOrder(raws[0]) : null;

            string? usedFen = branchFen;
            int anchor = -1;
            RenderedCluster? host = null;
            int hostSpot = -1;

            // Trägt der erste Zug eine Zugnummer, muss die Elternstellung zu ihr passen — sonst gilt der
            // Cluster hier als nicht spielbar. Grund: eine Verweis-Notiz („4...Sc6 5.Sc3 …") ist von der
            // Elternstellung aus ZUFÄLLIG legal (nach 1.e4 ist Sb8-c6 ein gültiger Zug) und wurde als
            // Variante ausgegeben, die etwas ganz anderes zeigt als der Satz meint; gemeldet 2026-10-09,
            // sie stand am Ende des Kapitels statt bei ihrem Zug. Ohne Nummer bleibt es beim Elternanker.
            bool parentFits = !ord.HasValue || FenHasOrder(branchFen, ord.Value);
            List<string>? moves = raws.Count > 0 && !hasNull && parentFits ? ResolveLine(branchFen, raws, 0) : null;

            // (1) Nicht von hier spielbar? Dann die Stellung der HAUPTLINIE suchen, zu der die
            // ZUGNUMMER des ersten Zuges passt (Vollzugzahl + Farbe müssen stimmen — ohne diese
            // Bedingung würde ein zufällig legaler Zug irgendwo anders angehängt).
            if (moves == null && !hasNull && ord.HasValue)
            {
                for (int a = 0; a < anchorFens.Count; a++)
                {
                    if (!FenHasOrder(anchorFens[a], ord.Value)) continue;
                    var tryMoves = ResolveLine(anchorFens[a], raws, 0);
                    if (tryMoves == null) continue;
                    moves = tryMoves;
                    usedFen = anchorFens[a];
                    anchor = a;
                    break;
                }
            }

            // (2) Sonst: Alternative zu einem Zug INNERHALB einer vorangegangenen Variante desselben
            // Blocks („… 3.Nc3 a6" und danach „3...h6", „7...Db6 ist ‚best'"). Die gehört genau dorthin
            // — gesammelt am Ende der Linie ergibt sie aneinandergereihte Satzfetzen ohne Zusammenhang.
            if (moves == null && !hasNull && ord.HasValue)
            {
                for (int r = rendered.Count - 1; r >= 0 && moves == null; r--)
                {
                    foreach (var spot in rendered[r].Spots)
                    {
                        if (spot.Order != ord.Value) continue;
                        var tryMoves = ResolveLine(spot.Fen, raws, 0);
                        if (tryMoves == null) continue;
                        moves = tryMoves;
                        usedFen = spot.Fen;
                        host = rendered[r];
                        hostSpot = spot.TokenIndex;
                        break;
                    }
                }
            }
            return (moves, usedFen, anchor, host, hostSpot);
        }

        /// <summary>Eine gebaute Variante: Token des Rumpfes, die Stellungen VOR ihren Zügen (Ankerpunkte für
        /// Unter-Varianten) und die Unter-Varianten je Zug-Token.</summary>
        private sealed class RenderedCluster
        {
            public List<string> Body { get; } = [];
            public List<(int Order, string Fen, int TokenIndex)> Spots { get; } = [];
            private readonly Dictionary<int, List<RenderedCluster>> _nested = [];

            /// <summary>
            /// Hängt eine Unter-Variante hinter den Zug <paramref name="moveTokenIndex"/>. Folgt danach noch
            /// ein ZUG, kommt sie direkt hinter den Zug: der Kommentar dazwischen leitet dann den nächsten
            /// Zug ein („… a6 {we meet the Kan with} 5.c4"), und die Variante dort hinein zu schieben
            /// zerschneidet den Satz. Ist der Zug dagegen der LETZTE der Variante, gehört der Kommentar zur
            /// Alternative selbst („… a6 {and even} (3...h6 …)") und die Variante kommt dahinter.
            /// </summary>
            public void AddNested(int moveTokenIndex, RenderedCluster child)
            {
                int at = moveTokenIndex;
                bool moreMoves = Spots.Any(sp => sp.TokenIndex > moveTokenIndex);
                if (!moreMoves)
                    while (at + 1 < Body.Count && Body[at + 1].StartsWith('{')) at++;
                if (!_nested.TryGetValue(at, out var list)) _nested[at] = list = [];
                list.Add(child);
            }

            public string ToPgn()
            {
                var sb = new StringBuilder("(");
                for (int i = 0; i < Body.Count; i++)
                {
                    if (sb.Length > 1) sb.Append(' ');
                    sb.Append(Body[i]);
                    if (!_nested.TryGetValue(i, out var kids)) continue;
                    foreach (var kid in kids) { sb.Append(' '); sb.Append(kid.ToPgn()); }
                }
                return sb.Append(')').ToString();
            }
        }

        /// <summary>Baut den Rumpf eines Clusters aus den schon aufgelösten Zügen und merkt sich dabei die
        /// Stellung vor jedem Zug — daran hängen später die Unter-Varianten.</summary>
        private static RenderedCluster? BuildCluster(List<JsonMoveItemList> cluster, List<string> moves, string fen)
        {
            ChessGame? game = SanResolver.TryNewGame(fen);
            if (game == null) return null;

            var rc = new RenderedCluster();
            int k = 0;
            foreach (var item in cluster)
            {
                if (item.Key == "C")
                {
                    string c = item.CommentAfter;
                    if (c != "") rc.Body.Add($"{{{c}}}");
                }
                else if (item.Key == "V")
                {
                    // Verschachtelte Variante → als Klartext einbetten (gültig + einfach).
                    string nested = FlattenToText(item);
                    if (nested != "") rc.Body.Add($"{{{nested}}}");
                }
                else if (item.Key == "S")
                {
                    if (SanTextOf(item) == "") continue;
                    if (k >= moves.Count) return null;
                    string fenBefore = game.GetFen();
                    rc.Spots.Add((OrderOfFen(fenBefore), fenBefore, rc.Body.Count));
                    var candidates = SanResolver.SanCandidates(game, StripMoveNumber(moves[k]));
                    if (candidates.Count == 0) return null;
                    try { game.MakeMove(candidates[0], false); }
                    catch { return null; }
                    rc.Body.Add(moves[k]);
                    k++;
                }
            }
            return rc.Body.Count > 0 ? rc : null;
        }

        /// <summary>Der ganze Cluster als Klartext — die Notlösung, wenn er nirgends spielbar ist.</summary>
        private static string PlainTextOf(List<JsonMoveItemList> cluster)
        {
            var sb = new StringBuilder();
            foreach (var item in cluster)
            {
                if (item.Key == "C") { string c = item.CommentAfter; if (c != "") AppendText(sb, c); }
                else if (item.Key == "V") { string n = FlattenToText(item); if (n != "") AppendText(sb, n); }
                else if (item.Key == "S") { string raw = SanTextOf(item); if (raw != "") AppendText(sb, raw); }
            }
            return sb.ToString().Trim();
        }

        /// <summary>Zugnummern-Schlüssel einer Stellung (<see cref="MoveOrder"/>: weiß = N·2, schwarz = N·2+1).</summary>
        private static int OrderOfFen(string fen)
        {
            var parts = (fen ?? "").Split(' ');
            if (parts.Length < 6 || !int.TryParse(parts[5], out int fullmove)) return -1;
            return fullmove * 2 + (parts[1] == "b" ? 1 : 0);
        }

        /// <summary>Passt die Stellung zur Zugnummer eines Tokens (<see cref="MoveOrder"/>: weiß = N·2,
        /// schwarz = N·2+1)? Gelesen wird nur die FEN selbst — Vollzugzahl und Seite am Zug.</summary>
        private static bool FenHasOrder(string fen, int order)
        {
            var parts = (fen ?? "").Split(' ');
            if (parts.Length < 6) return false;
            if (!int.TryParse(parts[5], out int fullmove)) return false;
            return fullmove * 2 + (parts[1] == "b" ? 1 : 0) == order;
        }

        /// <summary>Höchstzahl Halbzüge, die <see cref="ResolveLine"/> für EINEN Varianten-Cluster nachspielt (= Rekursionstiefe).
        /// 256 Vollzüge reichen für jede Seitenlinie eines Kurses; 512 Rahmen belegen nur einen Bruchteil des
        /// Threadpool-Stacks (1,5 MB unter Linux). Längere Cluster bleiben als Kommentar vollständig erhalten.</summary>
        private const int MaxVariationPlies = 512;

        /// <summary>Zugtext eines „S"-Eintrags. Mit Zeilenumbruch oder Klammer ist er kein Zug, sondern ein Versuch,
        /// Variante/Kommentar/Partie vorzeitig zu beenden → verworfen, wie ein leerer Eintrag (S2-017).</summary>
        private static string SanTextOf(JsonMoveItemList item)
        {
            string raw = ((item.Val?.ValueKind == JsonValueKind.String ? item.Val.Value.GetString() : "") ?? "").Trim();
            return PgnTokenGuard.HasLineBreakOrBracket(raw) ? "" : raw;
        }

        /// <summary>
        /// Spielt die Züge <paramref name="raws"/>[<paramref name="i"/>..] ab <paramref name="fen"/> und liefert sie
        /// so, wie sie ins PGN gehören — oder null, wenn die Folge nicht EINDEUTIG spielbar ist.
        ///
        /// <para>Autoren schreiben in ihren Seitenlinien auch mehrdeutige Züge („16.Ne4", wenn Springer auf c3 UND
        /// c5 nach e4 können; gemeldet 2026-09-23 an „Lifetime Repertoires: King's Indian Defense - Part 2").
        /// <see cref="SanResolver.SanToMove"/> nahm dann still den ersten Springer, und ins PGN kam ein Zug, den kein
        /// PGN-Leser spielen kann. Jetzt wird jede passende Lesart durchgespielt: trägt genau EINE die ganze Folge
        /// (nur nach 16.S3e4 geht 17.Sxb7), wird sie genommen und eindeutig geschrieben („16.N3e4"); tragen
        /// mehrere oder keine, wird der Cluster Kommentar — geraten wird nicht.</para>
        ///
        /// <para>Rekursiv je Zug: die Tiefe ist die Clusterlänge, und die kommt ungedeckelt aus den Daten (ein
        /// Cluster endet nur an einem Zugnummer-Rücksprung). Ein StackOverflow ist in .NET nicht fangbar und
        /// beendet den ganzen Prozess samt laufender Abrufe — deshalb wird ein Cluster mit mehr als
        /// <see cref="MaxVariationPlies"/> Zügen nicht nachgespielt, sondern Kommentar (Review 2026-09-29, N3-001).</para>
        /// </summary>
        private static List<string>? ResolveLine(string? fen, List<string> raws, int i)
        {
            if (raws.Count > MaxVariationPlies) return null;
            if (i >= raws.Count) return [];
            ChessGame? game = SanResolver.TryNewGame(fen);
            if (game == null) return null;

            string raw = raws[i];
            // Nur echte SAN wird Varianten-Zug: SanCandidates liest „N;f3" als Springerzug nach f3, im Movetext
            // beginnt „;" aber einen Zeilenkommentar. Alles andere bleibt als Kommentar erhalten (S2-017).
            string san = StripMoveNumber(raw);
            if (!PgnTokenGuard.IsSan(san)) return null;
            var candidates = SanResolver.SanCandidates(game, san);
            List<string>? rest = null;
            Move? chosen = null;
            foreach (var candidate in candidates)
            {
                ChessGame? next = SanResolver.TryNewGame(game.GetFen());
                if (next == null) continue;
                try { next.MakeMove(candidate, false); }
                catch { continue; }
                var tail = ResolveLine(next.GetFen(), raws, i + 1);
                if (tail == null) continue;
                if (rest != null) return null;   // zwei Lesarten tragen die ganze Folge → mehrdeutig
                rest = tail;
                chosen = candidate;
            }
            if (rest == null || chosen == null) return null;
            return [candidates.Count > 1 ? Disambiguate(raw, chosen, candidates) : raw, .. rest];
        }

        /// <summary>
        /// Schreibt einen mehrdeutigen Figurenzug eindeutig (SAN-Regel: erst die Linie, dann die Reihe, sonst
        /// beides): „16.Ne4" mit Springern auf c3/c5 → „16.N3e4". Zugnummer, Schlagzeichen und Suffix bleiben.
        /// </summary>
        private static string Disambiguate(string raw, Move chosen, List<Move> candidates)
        {
            var number = findLeadingMoveNumber().Match(raw);
            string prefix = number.Success ? raw[..number.Length] : "";
            string san = raw[prefix.Length..];
            var target = findTargetSquare().Match(san);
            if (san.Length == 0 || !char.IsUpper(san[0]) || !target.Success) return raw;

            char file = FileOf(chosen.OriginalPosition);
            int rank = chosen.OriginalPosition.Rank;
            var others = candidates
                .Where(c => FileOf(c.OriginalPosition) != file || c.OriginalPosition.Rank != rank)
                .ToList();
            string which = others.All(o => FileOf(o.OriginalPosition) != file) ? file.ToString()
                : others.All(o => o.OriginalPosition.Rank != rank) ? rank.ToString()
                : $"{file}{rank}";
            string capture = san[..target.Index].Contains('x') ? "x" : "";
            return prefix + san[0] + which + capture + san[target.Index..];
        }

        private static char FileOf(Position p) => char.ToLower(p.File.ToString()[0]);

        /// <summary>Flacht eine (verschachtelte) „V"-Struktur rein zu Text ab (Züge + Kommentare, ohne Klammern/FEN-Bezug).</summary>
        private static string FlattenToText(JsonMoveItemList item)
        {
            if (item.Val == null || item.Val.Value.ValueKind != JsonValueKind.Array) return "";
            var list = JsonSerializer.Deserialize<List<JsonMoveItemList>>(item.Val.Value, options: Options.GetOptions()) ?? [];
            var sb = new StringBuilder();
            foreach (var it in list)
            {
                if (it.Key == "S")
                {
                    string s = SanTextOf(it);
                    if (s != "") AppendText(sb, s);
                }
                else if (it.Key == "C") { string c = it.CommentAfter; if (c != "") AppendText(sb, c); }
                else if (it.Key == "V") { string n = FlattenToText(it); if (n != "") AppendText(sb, n); }
            }
            return sb.ToString().Trim();
        }

        /// <summary>Sortierschlüssel eines „N." / „N..."-Zugtokens (weiß = N·2, schwarz = N·2+1); null bei bloßer SAN.</summary>
        private static int? MoveOrder(string raw)
        {
            var mw = findWhiteMoveNumber().Match(raw);
            if (mw.Success) return int.Parse(mw.Groups[1].Value) * 2;
            var mb = findBlackMoveNumber().Match(raw);
            if (mb.Success) return int.Parse(mb.Groups[1].Value) * 2 + 1;
            return null;
        }

        /// <summary>Entfernt die führende Zugnummer („12." / „12...") aus einem Token; lässt bloße SAN unberührt.</summary>
        private static string StripMoveNumber(string raw)
        {
            var m = findLeadingMoveNumber().Match(raw);
            return m.Success ? raw[m.Length..].Trim() : raw.Trim();
        }

        private static void AppendText(StringBuilder sb, string s)
        {
            // Kein Leerzeichen vor einem Satzzeichen: der Klartext eines Clusters entsteht aus Zügen und
            // Textstücken, und ein Stück, das mit „." oder „," beginnt, gehört an das Wort davor
            // („1.d4 . And maybe this is true." → „1.d4. And maybe this is true.").
            if (sb.Length > 0 && !(s.Length > 0 && ",.;:!?)".Contains(s[0]))) sb.Append(' ');
            sb.Append(s);
        }

        [GeneratedRegex(@"^(\d+)\.(?!\.)")]
        private static partial Regex findWhiteMoveNumber();

        [GeneratedRegex(@"^(\d+)\.\.\.")]
        private static partial Regex findBlackMoveNumber();

        [GeneratedRegex(@"^\d+\.(\.\.)?\s*")]
        private static partial Regex findLeadingMoveNumber();

        /// <summary>Das ZIELfeld eines SAN-Zugs = das letzte Feld darin („N3xe4+" → e4).</summary>
        [GeneratedRegex(@"[a-h][1-8]", RegexOptions.RightToLeft)]
        private static partial Regex findTargetSquare();
    }
}
