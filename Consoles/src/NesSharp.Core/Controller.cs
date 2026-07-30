namespace NesSharp.Core;

[Flags]
public enum NesButtons : byte
{
    None = 0,
    A = 1 << 0,
    B = 1 << 1,
    Select = 1 << 2,
    Start = 1 << 3,
    Up = 1 << 4,
    Down = 1 << 5,
    Left = 1 << 6,
    Right = 1 << 7,
}

/// <summary>A standard NES controller's shift-register protocol. While strobe is held high
/// (bit 0 of a $4016 write), every read reports button A and continuously re-latches the
/// live button state. On the strobe's high-to-low transition, the current state is captured
/// once and reads shift it out one bit per read in order: A, B, Select, Start, Up, Down,
/// Left, Right. Reads past the 8th return 1, matching the real shift register's behavior
/// once it runs out of latched bits.</summary>
public sealed class Controller
{
    private NesButtons _buttonState;
    private byte _shiftRegister;
    private bool _strobe;

    public void SetButtons(NesButtons state) => _buttonState = state;

    public void Write(byte value)
    {
        _strobe = (value & 0x01) != 0;
        if (_strobe)
        {
            _shiftRegister = (byte)_buttonState;
        }
    }

    public byte Read()
    {
        if (_strobe)
        {
            _shiftRegister = (byte)_buttonState;
            return (byte)((byte)_buttonState & 0x01);
        }
        byte bit = (byte)(_shiftRegister & 0x01);
        _shiftRegister = (byte)((_shiftRegister >> 1) | 0x80);
        return bit;
    }
}
