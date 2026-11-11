using Cewka.Audio.Streaming;

namespace Cewka.Audio.Tests.Live;

/// <summary>Bajty i znaczniki dokładane przez test; pusta kolejka to „czekam”, a po Complete koniec.</summary>
internal sealed class FakeByteSource : ILiveByteSource
{
    private readonly Queue<object> _items = new();
    private int _offset;
    private bool _completed;

    public List<int> ReportedBitrates { get; } = [];

    public LiveFailure? Failure { get; private set; }

    public FakeByteSource Add(ReadOnlySpan<byte> data)
    {
        if (!data.IsEmpty) _items.Enqueue(data.ToArray());
        return this;
    }

    public FakeByteSource Add(StreamMarker marker)
    {
        _items.Enqueue(marker);
        return this;
    }

    public FakeByteSource Begin(LiveCodec codec = LiveCodec.Mp3) => Add(new StreamBegin(codec));

    public FakeByteSource Title(string? title) => Add(new StreamTitle(title));

    public void Complete(LiveFailure? failure = null)
    {
        _completed = true;
        Failure = failure;
    }

    public LiveReadStatus Read(Span<byte> destination, out int bytes, out StreamMarker? marker)
    {
        bytes = 0;
        marker = null;

        if (!_items.TryPeek(out var head))
            return _completed ? LiveReadStatus.Ended : LiveReadStatus.Waiting;

        if (head is StreamMarker found)
        {
            _items.Dequeue();
            marker = found;
            return LiveReadStatus.Data;
        }

        var data = (byte[])head;
        bytes = Math.Min(destination.Length, data.Length - _offset);
        data.AsSpan(_offset, bytes).CopyTo(destination);
        _offset += bytes;

        if (_offset == data.Length)
        {
            _items.Dequeue();
            _offset = 0;
        }

        return LiveReadStatus.Data;
    }

    public void ReportBitrate(int kilobits) => ReportedBitrates.Add(kilobits);

    /// <summary>Źródło z zapasowym adresem: czytelnik ma czekać na następne połączenie.</summary>
    public bool HasFallback { get; set; }

    public List<LiveFailure> Undecodable { get; } = [];

    public bool ReportUndecodable(LiveFailure failure)
    {
        Undecodable.Add(failure);
        return HasFallback;
    }
}
