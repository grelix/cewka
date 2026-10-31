using Cewka.Audio.Devices;

namespace Cewka.Audio.Tests.Engine;

/// <summary>
/// Wyjście dźwięku bez karty: test sam wywołuje wątek dźwiękowy, okres po okresie, i zbiera
/// wszystko, co silnik wysłał. Opcjonalnie wątek pompujący naśladuje urządzenie, gdy test
/// woła metodę czekającą na wątek dźwiękowy (pauza).
/// </summary>
internal sealed class TestOutput : IAudioOutput
{
    private readonly Lock _outputLock = new();
    private readonly List<float> _output = [];
    private readonly float[] _period;
    private Thread? _pump;
    private volatile bool _pumping;
    private volatile bool _running;

    public TestOutput(int periodFrames = 1024, int sampleRate = 48000, int channels = 2)
    {
        PeriodSizeInFrames = periodFrames;
        SampleRate = sampleRate;
        Channels = channels;
        _period = new float[periodFrames * channels];
    }

    public int SampleRate { get; }
    public int Channels { get; }
    public int PeriodSizeInFrames { get; }
    public string Name => "Atrapa";
    public bool IsRunning => _running;
    public bool IsFallback => false;
    public bool Disposed { get; private set; }
    public AudioDevice.RenderCallback? Render { get; set; }

    public event Action? Lost;

    public void Start() => _running = true;

    /// <summary>Jak w miniaudio: zatrzymanie czeka, aż trwające wywołanie zwrotne się skończy.</summary>
    public void Stop()
    {
        lock (_outputLock) _running = false;
    }

    /// <summary>Jeden okres wątku dźwiękowego; nic, gdy urządzenie stoi.</summary>
    public void Pump()
    {
        lock (_outputLock)
        {
            if (!_running) return;

            Array.Clear(_period);
            Render?.Invoke(_period);
            _output.AddRange(_period);
        }
    }

    /// <summary>Wątek naśladujący urządzenie: pompuje, dopóki trwa test.</summary>
    public void StartAutoPump()
    {
        _pumping = true;
        _pump = new Thread(() =>
        {
            while (_pumping)
            {
                Pump();
                Thread.Sleep(1);
            }
        }) { IsBackground = true };
        _pump.Start();
    }

    public void StopAutoPump()
    {
        _pumping = false;
        _pump?.Join();
    }

    /// <summary>Utrata urządzenia, tak jak zgłasza ją warstwa natywna.</summary>
    public void Lose()
    {
        _running = false;
        Lost?.Invoke();
    }

    /// <summary>
    /// Odbiorcy zgłoszenia utraty w tej chwili. Wywołani później naśladują zgłoszenie,
    /// które czekało w puli wątków, gdy urządzenie już zamknięto.
    /// </summary>
    public Action? LostHandlers => Lost;

    /// <summary>Kopia próbek wysłanych dotąd na wyjście, przeplatanych.</summary>
    public float[] Samples
    {
        get { lock (_outputLock) return _output.ToArray(); }
    }

    public void ClearSamples()
    {
        lock (_outputLock) _output.Clear();
    }

    public void Dispose()
    {
        StopAutoPump();
        _running = false;
        Disposed = true;
    }
}
