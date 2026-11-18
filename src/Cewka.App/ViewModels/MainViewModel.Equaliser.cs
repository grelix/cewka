using Cewka.App.Localisation;
using Cewka.App.Models;

namespace Cewka.App.ViewModels;

/// <summary>Tryb korektora, korektor parametryczny i profile dźwięku urządzeń.</summary>
public sealed partial class MainViewModel
{
    private bool _equaliserEnabled;
    private EqualiserMode _equaliserMode;

    /// <summary>Urządzenie, do którego należą bieżące wartości korektora i efektów; null, dopóki nieznane.</summary>
    private string? _profileDevice;

    /// <summary>Wartości są właśnie wczytywane z profilu; zapis do profilu czeka na koniec.</summary>
    private bool _loadingProfile;

    /// <summary>Korektor parametryczny: pasma, przedwzmacniacz trybu, import i eksport.</summary>
    public ParametricEqualiserViewModel Parametric { get; private set; } = null!;

    /// <summary>Przełącznik rodzaju korektora w nagłówku karty.</summary>
    public SegmentGroup EqualiserModeChoice { get; private set; } = null!;

    private void InitialiseEqualiser(AppSettings s)
    {
        _equaliserEnabled = s.EqualiserEnabled;
        _equaliserMode = Enum.IsDefined(s.EqualiserMode) ? s.EqualiserMode : EqualiserMode.Graphic;

        Parametric = new ParametricEqualiserViewModel(s.ParametricBands, s.ParametricPreamp);
        _engine.Graph.Parametric.SetBands(Parametric.Curve);
        _engine.Graph.Parametric.Preamp = Parametric.Preamp.Gain;
        Parametric.Changed += OnParametricChanged;
        Parametric.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ParametricEqualiserViewModel.CanReset)) Raise(nameof(CanResetEqualiser));
        };

        EqualiserModeChoice = new SegmentGroup(
            value => EqualiserMode = (EqualiserMode)value,
            ("EqualiserModeGraphic", EqualiserMode.Graphic),
            ("EqualiserModeParametric", EqualiserMode.Parametric));
        EqualiserModeChoice.Mark(_equaliserMode);

        ApplyEqualiserStages();

        // Pierwszy profil należy do urządzenia, na którym grano ostatnio, więc wspólne wartości są jego.
        _profileDevice = s.SoundProfilesEnabled ? s.SoundProfiles.FirstOrDefault()?.Device : null;
    }

    public bool EqualiserEnabled
    {
        get => _equaliserEnabled;
        set
        {
            if (_equaliserEnabled == value) return;

            _equaliserEnabled = value;
            _settings.Current.EqualiserEnabled = value;
            ApplyEqualiserStages();
            OnSoundChanged();
            Raise();
            Raise(nameof(EqualiserOpacity));
        }
    }

    public EqualiserMode EqualiserMode
    {
        get => _equaliserMode;
        set
        {
            if (_equaliserMode == value || !Enum.IsDefined(value)) return;

            _equaliserMode = value;
            _settings.Current.EqualiserMode = value;
            EqualiserModeChoice.Mark(value);
            ApplyEqualiserStages();
            OnSoundChanged();
            Raise();
            Raise(nameof(IsGraphicMode));
            Raise(nameof(IsParametricMode));
            Raise(nameof(ActivePreamp));
            Raise(nameof(CanResetEqualiser));
            Raise(nameof(EqualiserResetTooltip));
        }
    }

    public bool IsGraphicMode => _equaliserMode == EqualiserMode.Graphic;

    public bool IsParametricMode => _equaliserMode == EqualiserMode.Parametric;

    /// <summary>Przedwzmacniacz pod suwakiem po lewej: każdy tryb ma własny.</summary>
    public EqBandViewModel ActivePreamp => IsParametricMode ? Parametric.Preamp : Preamp;

    public double EqualiserOpacity => EqualiserEnabled ? 1.0 : 0.4;

    /// <summary>Polecenie zerowania ma sens tylko wtedy, gdy grający korektor odbiega od stanu po wyzerowaniu.</summary>
    public bool CanResetEqualiser => IsParametricMode
        ? Parametric.CanReset
        : Preamp.Gain != 0 || Bands.Any(band => band.Gain != 0);

    public string EqualiserResetTooltip =>
        Strings.Current[IsParametricMode ? "TooltipEqualiserResetParametric" : "TooltipEqualiserReset"];

    /// <summary>Graficzny: pasma i przedwzmacniacz na 0 dB. Parametryczny: bez pasm, przedwzmacniacz na 0 dB.</summary>
    public void ResetEqualiser()
    {
        if (IsParametricMode)
        {
            Parametric.Reset();
            return;
        }

        foreach (var band in Bands) band.Gain = 0;
        Preamp.Gain = 0;
    }

    /// <summary>Wczytuje pasma z pliku i przełącza na korektor parametryczny; wynik w <see cref="ParametricEqualiserViewModel.Message"/>.</summary>
    public void ImportEqualiser(string path)
    {
        if (Parametric.Import(path)) EqualiserMode = EqualiserMode.Parametric;
    }

    public void ExportEqualiser(string path) => Parametric.Export(path);

    /// <summary>Gra jeden z dwóch korektorów; drugi wygasa płynnie i potem przepuszcza próbki bez zmiany.</summary>
    private void ApplyEqualiserStages()
    {
        _engine.Graph.Equaliser.Enabled = _equaliserEnabled && _equaliserMode == EqualiserMode.Graphic;
        _engine.Graph.Parametric.Enabled = _equaliserEnabled && _equaliserMode == EqualiserMode.Parametric;
    }

    private void OnParametricChanged()
    {
        _engine.Graph.Parametric.SetBands(Parametric.Curve);
        _engine.Graph.Parametric.Preamp = Parametric.Preamp.Gain;
        _settings.Current.ParametricBands = Parametric.ToSettings();
        _settings.Current.ParametricPreamp = Parametric.Preamp.Gain;
        Raise(nameof(CanResetEqualiser));
        OnSoundChanged();
    }

    /// <summary>Wspólny koniec każdej zmiany korektora i efektów: zapis, profil urządzenia, kropka przetwarzania.</summary>
    private void OnSoundChanged()
    {
        _settings.Touch();
        MirrorSoundProfile();
        RaiseProcessing();
    }

    // ---------- Profile urządzeń ----------

    /// <summary>Korektor i efekty osobno dla każdego urządzenia wyjściowego.</summary>
    public bool SoundProfilesEnabled
    {
        get => _settings.Current.SoundProfilesEnabled;
        set
        {
            if (_settings.Current.SoundProfilesEnabled == value) return;

            _settings.Current.SoundProfilesEnabled = value;

            // Włączenie: bieżące wartości stają się profilem urządzenia, które gra, a gdy żadne
            // jeszcze nie gra, profilem tego, które otworzy się pierwsze. Wyłączenie zostawia
            // bieżące wartości jako wspólne, a profile w pliku.
            _profileDevice = value && _engine.IsDeviceOpen ? _engine.DeviceName : null;
            if (_profileDevice is not null) StoreProfile(_profileDevice);

            _settings.Touch();
            Raise();
            RaiseProfileDevice();
        }
    }

    public bool ShowSoundProfileDevice => SoundProfilesEnabled && _profileDevice is not null;

    public string SoundProfileDeviceText =>
        _profileDevice is null ? string.Empty : string.Format(Strings.Current["SoundProfileDevice"], _profileDevice);

    /// <summary>
    /// Sprawdza, czy gra inne urządzenie niż to, do którego należą bieżące wartości. Jeśli tak,
    /// wartości idą do profilu poprzedniego, a nowe przychodzą z profilu nowego; urządzenie widziane
    /// pierwszy raz dziedziczy bieżące. Wołane przy każdej zmianie stanu wyjścia i z zegara
    /// odświeżania, bo silnik może sam przejść na urządzenie zastępcze.
    /// </summary>
    internal void SyncSoundProfile()
    {
        if (!SoundProfilesEnabled || !_engine.IsDeviceOpen) return;

        var device = _engine.DeviceName;
        if (device == _profileDevice) return;

        if (_profileDevice is null)
        {
            StoreProfile(device);
        }
        else
        {
            StoreProfile(_profileDevice);
            if (FindProfile(device) is { } profile) LoadProfile(profile);
            StoreProfile(device);
        }

        _profileDevice = device;
        _settings.Touch();
        RaiseProfileDevice();
    }

    private SoundProfile? FindProfile(string device) =>
        _settings.Current.SoundProfiles.FirstOrDefault(profile => profile.Device == device);

    /// <summary>Zapisuje bieżące wartości jako profil urządzenia, na początku listy; najdawniej używany wypada.</summary>
    private void StoreProfile(string device)
    {
        var profiles = _settings.Current.SoundProfiles;
        profiles.RemoveAll(profile => profile.Device == device);
        profiles.Insert(0, SoundProfile.Capture(device, _settings.Current));
        if (profiles.Count > SoundProfile.MaxProfiles)
            profiles.RemoveRange(SoundProfile.MaxProfiles, profiles.Count - SoundProfile.MaxProfiles);
    }

    private void MirrorSoundProfile()
    {
        if (_loadingProfile || !SoundProfilesEnabled || _profileDevice is null) return;
        StoreProfile(_profileDevice);
    }

    /// <summary>Przenosi profil do suwaków i przełączników; tor wygładza zmianę, więc nie ma przeskoku.</summary>
    private void LoadProfile(SoundProfile source)
    {
        var profile = source.Clone();
        _loadingProfile = true;
        try
        {
            EqualiserEnabled = profile.EqualiserEnabled;
            Preamp.Gain = profile.Preamp;
            for (var i = 0; i < Bands.Count && i < profile.EqualiserGains.Length; i++) Bands[i].Gain = profile.EqualiserGains[i];
            Parametric.Replace(profile.ParametricBands, profile.ParametricPreamp);
            EqualiserMode = profile.EqualiserMode;

            foreach (var effect in SoundEffects)
            {
                var (enabled, strength) = effect.Key switch
                {
                    "Crossfeed" => (profile.CrossfeedEnabled, profile.CrossfeedStrength),
                    "Loudness" => (profile.LoudnessEnabled, profile.LoudnessStrength),
                    "VirtualBass" => (profile.VirtualBassEnabled, profile.VirtualBassStrength),
                    "DynamicRange" => (profile.DynamicRangeEnabled, profile.DynamicRangeStrength),
                    "StereoWidth" => (profile.StereoWidthEnabled, profile.StereoWidthStrength),
                    _ => (effect.Enabled, effect.Strength),
                };

                effect.Enabled = enabled;
                effect.Strength = strength;
            }
        }
        finally
        {
            _loadingProfile = false;
        }
    }

    private void RaiseProfileDevice()
    {
        Raise(nameof(ShowSoundProfileDevice));
        Raise(nameof(SoundProfileDeviceText));
    }

    /// <summary>Teksty złożone w kodzie części korektora, po zmianie języka.</summary>
    private void RefreshEqualiserText()
    {
        Parametric.RefreshLabels();
        EqualiserModeChoice.RefreshLabels();
        Raise(nameof(EqualiserResetTooltip));
        Raise(nameof(SoundProfileDeviceText));
    }
}
