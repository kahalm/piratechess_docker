namespace piratechess_lib
{
    // Chessable-Login-Antwort (reines DTO; aus Models.cs verschoben, Review 2026-09-29 S2-016).
    public partial class ResponseLogin
    {
        public string Jwt { get; set; } = string.Empty;

        public int Uid
        {
            get
            {
                return JwtHelper.ExtractUidFromToken(Jwt);
            }
        }
    }
}
