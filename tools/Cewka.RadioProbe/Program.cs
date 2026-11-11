namespace Cewka.RadioProbe;

/// <summary>
/// Narzędzie deweloperskie do sprawdzania katalogu stacji, uruchamiane ręcznie przed wydaniem.
/// Próbkuje strumienie w pamięci przez tę samą warstwę sieci co program: nigdy nie gra dźwięku
/// i nie zapisuje go na dysk.
/// </summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancel.Cancel();
        };

        return await ProbeApp.RunAsync(args, Console.Out, Console.Error, null, cancel.Token);
    }
}
