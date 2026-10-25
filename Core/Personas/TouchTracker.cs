using GamepadEmu.Protocol;

namespace GKME.Core.Personas;

/// <summary>
/// Touch bookkeeping shared by the two Sony personas: allocates a tracking id
/// when a contact appears and advances the report counter each frame. Both DS4
/// and DualSense expose the same two-slot contact model, so the logic is one
/// class instead of a copy per packer.
/// </summary>
internal sealed class TouchTracker
{
    private readonly byte[] _ids = new byte[2];
    private readonly bool[] _previousActive = new bool[2];
    private byte _nextId;
    private byte _counter;

    /// <summary>Report counter stamped on the frame just built.</summary>
    public byte CurrentCounter { get; private set; }

    /// <summary>Counter stamped on the previous frame.</summary>
    public byte PreviousCounter { get; private set; }

    /// <summary>Tracking id of the contact in <paramref name="slot"/>.</summary>
    public byte Id(int slot) => _ids[slot];

    /// <summary>Id a future contact will get, offset by <paramref name="offset"/>
    /// (used for the DS4's inactive placeholder records).</summary>
    public byte Peek(int offset) => (byte)((_nextId + offset) & 0x7F);

    /// <summary>Latches both counters and allocates ids for newly pressed
    /// contacts. Called once at the start of each report.</summary>
    public void BeginFrame(GamepadInput input)
    {
        PreviousCounter = CurrentCounter;
        CurrentCounter = (byte)(_counter & 0x7F);

        for (int i = 0; i < 2; i++)
        {
            bool isActive = i < input.Touches.Count && input.Touches[i].Active;
            if (isActive && !_previousActive[i])
                _ids[i] = (byte)(_nextId++ & 0x7F);
            _previousActive[i] = isActive;
        }

        _counter++;
    }
}
