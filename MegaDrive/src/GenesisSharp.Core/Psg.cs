namespace GenesisSharp.Core;

/// <summary>SN76489 PSG — register-level model only, no audio synthesis. Write-only on real
/// hardware. Three square-wave tone channels plus one noise channel, addressed by a single
/// write-only byte stream:
///
/// A byte with bit 7 set is a latch/data byte: bits 6-4 select one of 8 registers (0/2/4 =
/// tone channel 0/1/2 frequency's low 4 bits, 6 = noise control, 1/3/5/7 = channel 0/1/2/noise
/// volume — a direct 4-bit attenuation, needing no second byte), bits 3-0 are the data.
///
/// A byte with bit 7 clear is only meaningful right after latching a tone register: its low 6
/// bits become that channel's frequency's high 6 bits, completing the 10-bit value.
///
/// Actual audio synthesis lives in Psg.Synthesis.cs.</summary>
public sealed partial class Psg
{
    public ushort[] ToneFrequency { get; } = new ushort[3];
    public byte[] Volume { get; } = new byte[4]; // channels 0-2 (tone) + 3 (noise), 0 = loudest, 0xF = silent
    public byte NoiseControl { get; private set; }

    private int _latchedRegister;

    public void Reset()
    {
        Array.Clear(ToneFrequency);
        NoiseControl = 0;
        _latchedRegister = 0;
        ResetSynthesisState();

        // Sega's integrated PSG (the variant actually wired into Master System/Genesis/Game
        // Gear, as opposed to generic discrete SN76489 chips, which power on with random
        // register content) is documented (smspower.org's SN76489 reference) to power on with
        // tone/noise registers at zero -- Array.Clear above already matches that -- but volume
        // at all-ones, i.e. maximum attenuation/silence. 0, this array's Array.Clear default, is
        // the loudest setting on this chip, not silence, so a game's very first audio frames
        // (before its own boot code gets around to muting the PSG) would otherwise blare an
        // aliased near-max-volume tone/noise burst on all four channels -- audible as a short
        // "scratch" right when a ROM loads, confirmed via a live trace against real Sonic 1
        // (frames 0-3 at full volume before the boot code writes silence at frame 4).
        Array.Fill(Volume, (byte)0x0F);
    }

    public void Write(byte value)
    {
        if ((value & 0x80) != 0)
        {
            _latchedRegister = (value >> 4) & 0x07;

            switch (_latchedRegister)
            {
                case 0 or 2 or 4:
                    {
                        int channel = _latchedRegister >> 1;
                        ToneFrequency[channel] = (ushort)((ToneFrequency[channel] & 0x3F0) | (value & 0x0F));
                        break;
                    }
                case 6:
                    NoiseControl = (byte)(value & 0x0F);
                    break;
                default: // 1, 3, 5, 7
                    Volume[_latchedRegister >> 1] = (byte)(value & 0x0F);
                    break;
            }

            return;
        }

        if (_latchedRegister is 0 or 2 or 4)
        {
            int channel = _latchedRegister >> 1;
            ToneFrequency[channel] = (ushort)((ToneFrequency[channel] & 0x0F) | ((value & 0x3F) << 4));
        }
    }
}
