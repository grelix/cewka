using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text;
using Cewka.App.Localisation;
using Cewka.App.Models;
using Cewka.Audio.Dsp;

namespace Cewka.App.ViewModels;

/// <summary>
/// Korektor parametryczny w panelu dźwięku: lista pasm, przedwzmacniacz trybu, import i eksport.
/// Każda zmiana zgłasza <see cref="Changed"/>; tor i zapis ustawień obsługuje model odtwarzacza.
/// </summary>
public sealed class ParametricEqualiserViewModel : ObservableObject
{
    /// <summary>Pasmo dodawane przyciskiem: dzwon 1 kHz, płaski, dobroć jednej oktawy.</summary>
    public const double NewBandFrequency = 1000;

    public const double NewBandQ = 1.41;

    private bool _suspended;
    private string _message = string.Empty;
    private ParametricBand[] _curve = [];

    public ParametricEqualiserViewModel(IEnumerable<ParametricBandSettings> bands, double preamp)
    {
        Preamp = new EqBandViewModel("Preamp", ClampPreamp(preamp));
        Preamp.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EqBandViewModel.Gain)) OnChanged();
        };

        Bands = [];
        Bands.CollectionChanged += OnBandsChanged;

        _suspended = true;
        foreach (var band in bands.Take(ParametricEqualiser.MaxBands)) Bands.Add(ParametricBandViewModel.From(band));
        _suspended = false;
        UpdateCurve();
    }

    /// <summary>Pasma, przedwzmacniacz albo ich liczba się zmieniły.</summary>
    public event Action? Changed;

    public ObservableCollection<ParametricBandViewModel> Bands { get; }

    /// <summary>Przedwzmacniacz trybu parametrycznego; ten sam suwak co w trybie graficznym.</summary>
    public EqBandViewModel Preamp { get; }

    public bool HasBands => Bands.Count > 0;

    /// <summary>Nagłówki kolumn nad pustą listą są niewidoczne, ale zajmują miejsce.</summary>
    public double ColumnsOpacity => HasBands ? 1.0 : 0.0;

    public bool CanAddBand => Bands.Count < ParametricEqualiser.MaxBands;

    /// <summary>Pasma w postaci dla toru i wykresu; nowa tablica po każdej zmianie.</summary>
    public ParametricBand[] Curve
    {
        get => _curve;
        private set => Set(ref _curve, value);
    }

    /// <summary>Wynik ostatniego importu albo eksportu; znika przy następnej zmianie.</summary>
    public string Message
    {
        get => _message;
        private set
        {
            if (!Set(ref _message, value)) return;
            Raise(nameof(HasMessage));
        }
    }

    public bool HasMessage => _message.Length > 0;

    /// <summary>Coś odbiega od stanu po wyzerowaniu: są pasma albo przedwzmacniacz nie stoi na zerze.</summary>
    public bool CanReset => Bands.Count > 0 || Preamp.Gain != 0;

    public static double ClampPreamp(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, ParametricEqualiser.MinPreamp, ParametricEqualiser.MaxPreamp) : 0;

    public void AddBand()
    {
        if (!CanAddBand) return;
        Bands.Add(new ParametricBandViewModel(ParametricFilterType.Peak, NewBandFrequency, 0, NewBandQ));
    }

    public void RemoveBand(ParametricBandViewModel band) => Bands.Remove(band);

    /// <summary>Usuwa pasma i zeruje przedwzmacniacz trybu.</summary>
    public void Reset() => Replace([], 0);

    /// <summary>Zastępuje wszystko naraz, z jednym zgłoszeniem zmiany: profil urządzenia, import, zerowanie.</summary>
    public void Replace(IEnumerable<ParametricBandSettings> bands, double preamp)
    {
        _suspended = true;
        try
        {
            // Wyczyszczenie listy nie podaje usuniętych pasm, więc odłączenie idzie przed nim.
            foreach (var band in Bands) band.PropertyChanged -= OnBandPropertyChanged;
            Bands.Clear();
            foreach (var band in bands.Take(ParametricEqualiser.MaxBands)) Bands.Add(ParametricBandViewModel.From(band));
            Preamp.Gain = ClampPreamp(preamp);
        }
        finally
        {
            _suspended = false;
        }

        OnChanged();
    }

    public List<ParametricBandSettings> ToSettings() => [.. Bands.Select(band => band.ToSettings())];

    /// <summary>
    /// Wczytuje plik w formacie Equalizer APO. Pasma i przedwzmacniacz trybu zostają zastąpione
    /// tylko wtedy, gdy plik ma co najmniej jedno obsługiwane pasmo.
    /// </summary>
    /// <returns>True, gdy pasma zastąpiono.</returns>
    public bool Import(string path)
    {
        EqualizerApoPreset preset;
        try
        {
            preset = EqualizerApoFile.Read(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                       or NotSupportedException or ArgumentException)
        {
            Console.Error.WriteLine($"[cewka] import korektora: {ex.Message}");
            Message = Strings.Current["EqualiserImportFailed"];
            return false;
        }

        if (preset.Bands.Count == 0)
        {
            Message = Strings.Current["EqualiserImportEmpty"];
            return false;
        }

        var preamp = preset.Preamp ?? 0;
        var clamped = ClampPreamp(preamp);

        Replace(preset.Bands.Select(band => new ParametricBandSettings
        {
            Type = band.Type, Frequency = band.Frequency, Gain = band.Gain, Q = band.Q, Enabled = band.Enabled,
        }), clamped);

        var text = new StringBuilder(preset.Skipped > 0
            ? string.Format(Strings.Current.Numbers, Strings.Current["EqualiserImportedPartly"], preset.Bands.Count, preset.Skipped)
            : string.Format(Strings.Current.Numbers, Strings.Current["EqualiserImported"], preset.Bands.Count));

        if (clamped != preamp)
            text.Append(' ').Append(Strings.Current[preamp < 0 ? "EqualiserImportPreampLimited" : "EqualiserImportPreampLimitedHigh"]);

        Message = text.ToString();
        return true;
    }

    /// <summary>Zapisuje pasma i przedwzmacniacz trybu w formacie Equalizer APO (UTF-8).</summary>
    public bool Export(string path)
    {
        try
        {
            File.WriteAllText(path, EqualizerApoFile.Format(Preamp.Gain, _curve), new UTF8Encoding(false));
            Message = string.Format(Strings.Current["EqualiserExported"], Path.GetFileName(path));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                       or ArgumentException or System.Security.SecurityException)
        {
            Console.Error.WriteLine($"[cewka] eksport korektora: {ex.Message}");
            Message = Strings.Current["EqualiserExportFailed"];
            return false;
        }
    }

    /// <summary>Po zmianie języka: nazwy pasm i zapis liczb.</summary>
    public void RefreshLabels()
    {
        Preamp.RefreshText();
        foreach (var band in Bands) band.RefreshLabels();
    }

    private void OnBandsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (ParametricBandViewModel band in e.OldItems) band.PropertyChanged -= OnBandPropertyChanged;

        if (e.NewItems is not null)
            foreach (ParametricBandViewModel band in e.NewItems) band.PropertyChanged += OnBandPropertyChanged;

        for (var i = 0; i < Bands.Count; i++) Bands[i].Number = i + 1;

        Raise(nameof(HasBands));
        Raise(nameof(ColumnsOpacity));
        Raise(nameof(CanAddBand));
        OnChanged();
    }

    private void OnBandPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ParametricBandViewModel.Type) or nameof(ParametricBandViewModel.Frequency)
            or nameof(ParametricBandViewModel.Gain) or nameof(ParametricBandViewModel.Q)
            or nameof(ParametricBandViewModel.Enabled))
            OnChanged();
    }

    private void OnChanged()
    {
        if (_suspended) return;

        Message = string.Empty;
        UpdateCurve();
        Raise(nameof(CanReset));
        Changed?.Invoke();
    }

    private void UpdateCurve() => Curve = [.. Bands.Select(band => band.ToBand())];
}
