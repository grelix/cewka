namespace Cewka.Audio.Streaming;

internal interface IIcySink
{
    void OnAudio(ReadOnlySpan<byte> audio);
    void OnMetadata(ReadOnlySpan<byte> block);
}

/// <summary>
/// Rozdziela dźwięk i bloki metadanych ICY: po metaInt bajtach dźwięku bajt długości L,
/// potem L × 16 bajtów metadanych. Porcje sieci mogą dzielić strumień w dowolnym miejscu.
/// </summary>
internal sealed class IcyDemuxer(int metaInt, IIcySink sink)
{
    private const int MaxBlockBytes = 255 * 16;

    private enum Part { Audio, Length, Metadata }

    private readonly byte[] _block = new byte[MaxBlockBytes];
    private Part _part = Part.Audio;
    private readonly int _metaInt = metaInt;
    private int _audioLeft = metaInt;
    private int _blockLength;
    private int _blockFilled;

    /// <summary>Odbiorca może się zmienić między porcjami: rozpoznanie treści oddaje strumień sesji.</summary>
    public IIcySink Sink { get; set; } = sink;

    public void Push(ReadOnlySpan<byte> input)
    {
        while (!input.IsEmpty)
        {
            switch (_part)
            {
                case Part.Audio:
                    var audio = Math.Min(_audioLeft, input.Length);
                    Sink.OnAudio(input[..audio]);
                    input = input[audio..];
                    _audioLeft -= audio;
                    if (_audioLeft == 0)
                        _part = Part.Length;
                    break;

                case Part.Length:
                    _blockLength = input[0] * 16;
                    input = input[1..];
                    _blockFilled = 0;
                    if (_blockLength > 0)
                    {
                        _part = Part.Metadata;
                    }
                    else
                    {
                        _audioLeft = _metaInt;
                        _part = Part.Audio;
                    }

                    break;

                case Part.Metadata:
                    var count = Math.Min(_blockLength - _blockFilled, input.Length);
                    input[..count].CopyTo(_block.AsSpan(_blockFilled));
                    input = input[count..];
                    _blockFilled += count;
                    if (_blockFilled == _blockLength)
                    {
                        Sink.OnMetadata(_block.AsSpan(0, _blockLength));
                        _audioLeft = _metaInt;
                        _part = Part.Audio;
                    }

                    break;
            }
        }
    }
}
