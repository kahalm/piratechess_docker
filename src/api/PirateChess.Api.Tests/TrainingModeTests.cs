using PirateChess.Api.Services;

namespace PirateChess.Api.Tests;

/// <summary>S2-015: Prüfung und Abbildung des Trainingsmodus an einer Stelle — mit derselben Strenge wie die frühere
/// Namensliste (exakter Name, keine Zahlen, keine andere Schreibweise).</summary>
public class TrainingModeTests
{
    [Theory]
    [InlineData("AllKeyMoves", TrainingMode.AllKeyMoves)]
    [InlineData("FirstKeyMove", TrainingMode.FirstKeyMove)]
    [InlineData("None", TrainingMode.None)]
    public void TryParse_ExactName(string value, TrainingMode expected)
    {
        Assert.True(TrainingModes.TryParse(value, out var mode));
        Assert.Equal(expected, mode);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("FIRSTKEYMOVE")]
    [InlineData("0")]
    [InlineData("2")]
    [InlineData(" None")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Nonsense")]
    public void TryParse_RejectsEverythingElse(string? value)
        => Assert.False(TrainingModes.TryParse(value, out _));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParseOrDefault_EmptyUsesFallback(string? value)
    {
        Assert.True(TrainingModes.TryParseOrDefault(value, TrainingMode.FirstKeyMove, out var mode));
        Assert.Equal(TrainingMode.FirstKeyMove, mode);
    }

    [Fact]
    public void TryParseOrDefault_InvalidStaysInvalid()
        => Assert.False(TrainingModes.TryParseOrDefault("Nonsense", TrainingMode.None, out _));

    [Theory]
    [InlineData(TrainingMode.AllKeyMoves, true, false)]
    [InlineData(TrainingMode.FirstKeyMove, false, false)]
    [InlineData(TrainingMode.None, false, true)]
    public void ApplyTo_SetsLibraryFlags(TrainingMode mode, bool allKeyMoves, bool noTrainingMove)
    {
        // Gegenwerte vorbelegen: ApplyTo muss beide Flags setzen, nicht nur eines.
        var lib = new piratechess_lib.PirateChessLib { AllKeyMovesTraining = !allKeyMoves, NoTrainingMove = !noTrainingMove };

        mode.ApplyTo(lib);

        Assert.Equal(allKeyMoves, lib.AllKeyMovesTraining);
        Assert.Equal(noTrainingMove, lib.NoTrainingMove);
    }
}
