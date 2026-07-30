namespace NesSharp.Core;

/// <summary>
/// Namco 106/163 (mapper 19) — Rolling Thunder, Dragon Spirit, Family Circuit, Digital
/// Devil Monogatari (mostly Japan-exclusive titles). Eight independent 1KB CHR banks, three
/// switchable 8KB PRG banks (fixed last 8KB at $E000-$FFFF), four "nametable bank" registers
/// that can select either an internal CIRAM page or (in real hardware) substitute CHR-ROM
/// data as nametable content for special effects, a 15-bit up-counting IRQ, and this chip's
/// defining feature: up to 8 wavetable audio channels reading 4-bit samples out of a
/// 128-byte internal RAM shared with the channels' own frequency/volume/waveform-address
/// control fields — real hardware time-multiplexes processing across however many channels
/// are active, trading channel count for effective sample rate.
///
/// Confidence notes (this mapper has the lowest confidence of any implemented here):
///  - PRG/CHR banking and the IRQ counter are implemented from documented specs at
///    reasonable confidence — these determine whether a game runs at all.
///  - The "nametable bank can substitute CHR-ROM as nametable data" feature is NOT
///    implemented; nametable-bank register writes are stored but only their low bit (CIRAM
///    page select) is used, inferring the closest standard mirroring pattern from the four
///    registers — see <see cref="Mirroring"/>. Games relying on the CHR-as-nametable trick
///    for effects will render incorrectly.
///  - The wavetable audio's exact internal-RAM byte layout (which bytes/bits hold frequency,
///    volume, waveform address/length, and active-channel count) is a best-effort
///    reconstruction from a layout commonly described for this chip, not verified against a
///    reference — unlike every other mapper here, there was no test ROM available to check
///    it against. The real per-channel timing (audio quality depends on how many of the 8
///    channels are active) is approximated with a fixed clock divider instead.
/// </summary>
public sealed class Mapper19 : IMapper
{
    private const int ChannelRegBase = 0x40;

    private readonly byte[] _prg;
    private readonly byte[] _chr;
    private readonly bool _chrIsRam;

    private readonly byte[] _chrBanks = new byte[8];
    private readonly byte[] _nametableBanks = new byte[4];
    private byte _prgBank0;
    private byte _prgBank1;
    private byte _prgBank2;

    private ushort _irqCounter;
    private bool _irqEnabled;
    private bool _irqPending;

    private readonly byte[] _soundRam = new byte[128];
    private byte _soundRamAddress;
    private bool _soundRamAutoIncrement;
    private readonly uint[] _phase = new uint[8];
    private int _audioDivider;

    public Mapper19(Cartridge cartridge)
    {
        _prg = cartridge.Prg;
        _chrIsRam = cartridge.Chr.Length == 0;
        _chr = _chrIsRam ? new byte[8 * 1024] : cartridge.Chr;
    }

    public MirroringMode Mirroring
    {
        get
        {
            int p0 = _nametableBanks[0] & 1;
            int p1 = _nametableBanks[1] & 1;
            int p2 = _nametableBanks[2] & 1;
            int p3 = _nametableBanks[3] & 1;
            if (p0 == p1 && p1 == p2 && p2 == p3)
            {
                return p0 == 0 ? MirroringMode.SingleScreenLower : MirroringMode.SingleScreenUpper;
            }
            if (p0 == p1 && p2 == p3)
            {
                return MirroringMode.Horizontal;
            }
            if (p0 == p2 && p1 == p3)
            {
                return MirroringMode.Vertical;
            }
            return MirroringMode.Horizontal;
        }
    }

    public bool IrqLine => _irqPending;

    public byte CpuRead(ushort address)
    {
        if (address is >= 0x4800 and < 0x5000)
        {
            byte value = _soundRam[_soundRamAddress & 0x7F];
            if (_soundRamAutoIncrement)
            {
                _soundRamAddress = (byte)((_soundRamAddress + 1) & 0x7F);
            }
            return value;
        }
        if (address is >= 0x5000 and < 0x5800)
        {
            return (byte)_irqCounter;
        }
        if (address is >= 0x5800 and < 0x6000)
        {
            return (byte)((_irqCounter >> 8) | (_irqEnabled ? 0x80 : 0));
        }
        if (address < 0x8000)
        {
            return 0;
        }

        int bankCount8k = _prg.Length / 0x2000;
        int bank = address switch
        {
            < 0xA000 => _prgBank0 % bankCount8k,
            < 0xC000 => _prgBank1 % bankCount8k,
            < 0xE000 => _prgBank2 % bankCount8k,
            _ => bankCount8k - 1,
        };
        return _prg[bank * 0x2000 + (address & 0x1FFF)];
    }

    public void CpuWrite(ushort address, byte value)
    {
        if (address is >= 0x4800 and < 0x5000)
        {
            _soundRam[_soundRamAddress & 0x7F] = value;
            if (_soundRamAutoIncrement)
            {
                _soundRamAddress = (byte)((_soundRamAddress + 1) & 0x7F);
            }
            return;
        }
        if (address is >= 0x5000 and < 0x5800)
        {
            _irqCounter = (ushort)((_irqCounter & 0x7F00) | value);
            _irqPending = false;
            return;
        }
        if (address is >= 0x5800 and < 0x6000)
        {
            _irqCounter = (ushort)((_irqCounter & 0x00FF) | ((value & 0x7F) << 8));
            _irqEnabled = (value & 0x80) != 0;
            _irqPending = false;
            return;
        }
        if (address < 0x8000)
        {
            return;
        }

        switch (address & 0xF800)
        {
            case 0x8000: case 0x8800: case 0x9000: case 0x9800:
            case 0xA000: case 0xA800: case 0xB000: case 0xB800:
                _chrBanks[(address - 0x8000) / 0x800] = value;
                break;
            case 0xC000: case 0xC800: case 0xD000: case 0xD800:
                _nametableBanks[(address - 0xC000) / 0x800] = value;
                break;
            case 0xE000:
                _prgBank0 = value;
                break;
            case 0xE800:
                _prgBank1 = value;
                break;
            case 0xF000:
                _prgBank2 = value;
                break;
            case 0xF800:
                _soundRamAddress = (byte)(value & 0x7F);
                _soundRamAutoIncrement = (value & 0x80) != 0;
                break;
        }
    }

    public byte PpuRead(ushort address)
    {
        int bankCount1k = _chr.Length / 0x400;
        int bank = _chrBanks[address / 0x400] % bankCount1k;
        return _chr[bank * 0x400 + (address & 0x3FF)];
    }

    public void PpuWrite(ushort address, byte value)
    {
        if (!_chrIsRam)
        {
            return;
        }
        int bankCount1k = _chr.Length / 0x400;
        int bank = _chrBanks[address / 0x400] % bankCount1k;
        _chr[bank * 0x400 + (address & 0x3FF)] = value;
    }

    public void ClockCpu()
    {
        if (_irqEnabled && _irqCounter < 0x7FFF)
        {
            _irqCounter++;
            if (_irqCounter == 0x7FFF)
            {
                _irqPending = true;
            }
        }

        _audioDivider++;
        if (_audioDivider >= 15)
        {
            _audioDivider = 0;
            ClockActiveChannels();
        }
    }

    private void ClockActiveChannels()
    {
        int count = GetChannelCount();
        for (int ch = 8 - count; ch < 8; ch++)
        {
            _phase[ch] = (_phase[ch] + GetFrequency(ch)) & 0x3FFFF;
        }
    }

    private uint GetFrequency(int ch)
    {
        int b = ChannelRegBase + ch * 8;
        uint lo = _soundRam[b];
        uint mid = _soundRam[b + 2];
        uint hi = (uint)(_soundRam[b + 4] & 0x03);
        return lo | (mid << 8) | (hi << 16);
    }

    private int GetWaveformLength(int ch) => 4 * (((_soundRam[ChannelRegBase + ch * 8 + 4] >> 2) & 0x07) + 1);

    private int GetVolume(int ch) => _soundRam[ChannelRegBase + ch * 8 + 6] & 0x0F;

    private int GetWaveformAddress(int ch) => _soundRam[ChannelRegBase + ch * 8 + 7];

    private int GetChannelCount() => (((_soundRam[ChannelRegBase + 7 * 8 + 6] >> 4) & 0x07) + 1);

    private int ReadWaveformSample(int ch)
    {
        int length = GetWaveformLength(ch);
        int position = (int)((_phase[ch] >> 10) % (uint)length);
        int nibbleOffset = (GetWaveformAddress(ch) + position) & 0xFF;
        byte ramByte = _soundRam[nibbleOffset / 2];
        return nibbleOffset % 2 == 0 ? ramByte & 0x0F : (ramByte >> 4) & 0x0F;
    }

    /// <summary>Each active channel's 4-bit sample (conventionally centered at 8 = silence)
    /// is scaled by that channel's volume, summed, and normalized by the active channel
    /// count with a modest overall weight — approximating, rather than replicating, real
    /// hardware's time-multiplexed mixing.</summary>
    public float GetAudioSample()
    {
        int count = GetChannelCount();
        float sum = 0f;
        for (int ch = 8 - count; ch < 8; ch++)
        {
            int volume = GetVolume(ch);
            if (volume == 0)
            {
                continue;
            }
            int sample = ReadWaveformSample(ch);
            float centered = (sample - 8) / 8f;
            sum += centered * (volume / 15f);
        }
        return sum / count * 0.5f;
    }
}
