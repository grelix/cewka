using Cewka.App.ViewModels;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>
/// Tests for the position restored from the previous session.
///
/// <para>Te testy istnieją z konkretnego powodu: usterka, w której pierwsze naciśnięcie pauzy
/// po uruchomieniu utworu kliknięciem w kolejkę zamiast zatrzymania przeskakiwało na pozycję
/// z poprzedniej sesji, przeszła niezauważona przez cały etap 6 i 7. Trzydzieści testów, jakie
/// wtedy istniały, dotyczyło wyłącznie przetwarzania sygnału.</para>
/// </summary>
public class PendingResumeTests
{
    [Fact]
    public void NieUzbrojonyNieDajeNic()
    {
        var resume = new PendingResume();

        Assert.False(resume.IsArmed);
        Assert.False(resume.TryTake(alreadyPlaying: false, out _, out _));
    }

    [Fact]
    public void UzbrojonyOddajeZapamietanaPozycje()
    {
        var resume = new PendingResume();
        resume.Arm(index: 2, seconds: 91.5, queueLength: 5);

        Assert.True(resume.IsArmed);
        Assert.True(resume.TryTake(alreadyPlaying: false, out var index, out var seconds));

        Assert.Equal(2, index);
        Assert.Equal(91.5, seconds, precision: 3);
    }

    [Fact]
    public void PoZuzyciuNieWracaPrzyNastepnymNacisnieciu()
    {
        var resume = new PendingResume();
        resume.Arm(index: 0, seconds: 30, queueLength: 1);

        Assert.True(resume.TryTake(alreadyPlaying: false, out _, out _));
        Assert.False(resume.IsArmed);
        Assert.False(resume.TryTake(alreadyPlaying: false, out _, out _));
    }

    /// <summary>
    /// Sedno naprawionej usterki: gdy odtwarzanie już trwa, przywracanie musi ustąpić — inaczej
    /// pauza przeskakuje na pozycję z poprzedniej sesji zamiast zatrzymać utwór.
    /// </summary>
    [Fact]
    public void PodczasOdtwarzaniaUstepujeIWygasa()
    {
        var resume = new PendingResume();
        resume.Arm(index: 0, seconds: 60, queueLength: 2);

        Assert.False(resume.TryTake(alreadyPlaying: true, out _, out _));

        // Ma wygasnąć także wtedy, gdy ustąpiło: powrót przy kolejnym naciśnięciu byłby
        // dokładnie tą samą usterką, tylko przesuniętą o jedno kliknięcie.
        Assert.False(resume.IsArmed);
        Assert.False(resume.TryTake(alreadyPlaying: false, out _, out _));
    }

    [Fact]
    public void PorzuceniePrzedNacisnieciemUniemozliwiaPrzeskok()
    {
        var resume = new PendingResume();
        resume.Arm(index: 1, seconds: 45, queueLength: 3);

        // Tak zachowuje się kliknięcie w kolejkę, następny i poprzedni utwór.
        resume.Discard();

        Assert.False(resume.IsArmed);
        Assert.False(resume.TryTake(alreadyPlaying: false, out _, out _));
    }

    [Theory]
    [InlineData(-1, 3)]
    [InlineData(3, 3)]
    [InlineData(7, 3)]
    [InlineData(0, 0)]
    public void PozycjaPozaKolejkaJestOdrzucana(int index, int queueLength)
    {
        var resume = new PendingResume();
        resume.Arm(index, seconds: 10, queueLength);

        Assert.False(resume.IsArmed);
    }

    [Fact]
    public void PonowneUzbrojenieNadpisujePoprzedniaPozycje()
    {
        var resume = new PendingResume();
        resume.Arm(index: 0, seconds: 10, queueLength: 4);
        resume.Arm(index: 3, seconds: 200, queueLength: 4);

        Assert.True(resume.TryTake(alreadyPlaying: false, out var index, out var seconds));
        Assert.Equal(3, index);
        Assert.Equal(200, seconds, precision: 3);
    }
}
