namespace NesSharp.Core;

/// <summary>
/// A Ricoh 2C02: registers, VRAM access, VBlank/NMI timing, and background + sprite
/// rendering into a 256x240 framebuffer of 6-bit NES system palette indices (see
/// <see cref="NesPalette"/> for the RGB lookup — this class only ever outputs indices,
/// matching how the real PPU hands the video DAC a palette index and nothing else).
///
/// The background pipeline follows the well-documented NESdev "PPU scrolling" and "PPU
/// rendering" reference model: a shared 15-bit "current"/"temporary" VRAM address pair
/// (v/t) plus a 3-bit fine-X, fed by PPUSCROLL/PPUADDR/PPUCTRL, and two 16-bit shift
/// registers per plane (pattern low/high, attribute low/high) reloaded every 8 dots and
/// shifted every dot. Sprite evaluation/fetch is done once per scanline in a single pass
/// (not hardware's literal per-dot secondary-OAM state machine) — functionally correct
/// output (8-sprite limit, overflow flag, priority, sprite-0 hit) without replicating the
/// real hardware's specific overflow-detection bug or exact OAM-corruption side effects.
///
/// Deliberately out of scope for this milestone (documented, not forgotten):
///  - The $2002-read/VBlank-set race condition and other sub-cycle PPU/CPU interleaving.
///  - Exact per-dot PPU-internal bus timing (matters for MMC3-style scanline IRQ mappers,
///    not for NROM).
///  - Emphasis/grayscale bits in PPUMASK (top 2 bits of a palette byte).
/// </summary>
public sealed class Ppu2C02
{
    private readonly IMapper _mapper;
    private readonly byte[] _nametables = new byte[2048];
    private readonly byte[] _paletteRam = new byte[32];
    private readonly byte[] _oam = new byte[256];

    private byte _ctrl;
    private byte _mask;
    private bool _statusVBlank;
    private bool _statusSpriteZeroHit;
    private bool _statusSpriteOverflow;
    private byte _oamAddr;
    private bool _addrLatch; // shared "w" toggle for PPUSCROLL/PPUADDR
    private ushort _vramAddr; // "v"
    private ushort _tempAddr; // "t"
    private byte _fineX;
    private byte _dataBuffer;
    private bool _oddFrame;
    private bool _nmiRequested;

    // Background fetch latches and shift registers.
    private byte _bgNextTileId;
    private byte _bgNextTileAttrib;
    private byte _bgNextTileLsb;
    private byte _bgNextTileMsb;
    private ushort _bgShifterPatternLo;
    private ushort _bgShifterPatternHi;
    private ushort _bgShifterAttribLo;
    private ushort _bgShifterAttribHi;

    // Sprites visible on the scanline currently being drawn (populated at dot 257 of the
    // previous scanline).
    private const int MaxSpritesPerScanline = 8;
    private readonly byte[] _spritePatternLo = new byte[MaxSpritesPerScanline];
    private readonly byte[] _spritePatternHi = new byte[MaxSpritesPerScanline];
    private readonly byte[] _spriteAttrib = new byte[MaxSpritesPerScanline];
    private readonly byte[] _spriteX = new byte[MaxSpritesPerScanline];
    private readonly bool[] _spriteIsZero = new bool[MaxSpritesPerScanline];
    private int _spriteCount;

    public int Scanline { get; private set; }
    public int Dot { get; private set; }
    public long FrameCount { get; private set; }

    /// <summary>256x240 framebuffer of 6-bit NES palette indices, row-major (y*256+x).</summary>
    public byte[] Frame { get; } = new byte[256 * 240];

    public Ppu2C02(IMapper mapper)
    {
        _mapper = mapper;
    }

    public void Tick()
    {
        bool renderingEnabled = (_mask & 0x18) != 0;
        bool visibleScanline = Scanline is >= 0 and <= 239;
        bool preRenderScanline = Scanline == 261;

        if (visibleScanline && Dot is >= 1 and <= 256)
        {
            RenderPixel(Dot - 1, Scanline);
        }

        if (renderingEnabled && (visibleScanline || preRenderScanline))
        {
            if (Dot is >= 1 and <= 256 or >= 321 and <= 336)
            {
                UpdateBackgroundShifters();
                switch (Dot % 8)
                {
                    case 1: LoadBackgroundShiftersFromLatches(); FetchNametableByte(); break;
                    case 3: FetchAttributeByte(); break;
                    case 5: FetchPatternLowByte(); break;
                    case 7: FetchPatternHighByte(); break;
                    case 0: IncrementCoarseX(); break;
                }
            }
            if (Dot == 256)
            {
                IncrementY();
            }
            if (Dot == 257)
            {
                CopyHorizontalBits();
            }
            if (preRenderScanline && Dot is >= 280 and <= 304)
            {
                CopyVerticalBits();
            }
        }

        if (visibleScanline && Dot == 257)
        {
            EvaluateSpritesForNextScanline();
        }

        Dot++;
        if (preRenderScanline && Dot == 340 && renderingEnabled && _oddFrame)
        {
            Dot = 0;
            Scanline = 0;
            _oddFrame = !_oddFrame;
            FrameCount++;
        }
        else if (Dot > 340)
        {
            Dot = 0;
            Scanline++;
            if (Scanline > 261)
            {
                Scanline = 0;
                _oddFrame = !_oddFrame;
                FrameCount++;
            }
        }

        if (Scanline == 241 && Dot == 1)
        {
            _statusVBlank = true;
            if ((_ctrl & 0x80) != 0)
            {
                _nmiRequested = true;
            }
        }
        else if (Scanline == 261 && Dot == 1)
        {
            _statusVBlank = false;
            _statusSpriteZeroHit = false;
            _statusSpriteOverflow = false;
        }
    }

    /// <summary>Returns true (once) if an NMI should be raised, clearing the request.</summary>
    public bool TakeNmiRequest()
    {
        if (!_nmiRequested)
        {
            return false;
        }
        _nmiRequested = false;
        return true;
    }

    public byte ReadRegister(int index)
    {
        switch (index)
        {
            case 2: // PPUSTATUS
            {
                byte value = (byte)(
                    (_statusVBlank ? 0x80 : 0) |
                    (_statusSpriteZeroHit ? 0x40 : 0) |
                    (_statusSpriteOverflow ? 0x20 : 0));
                _statusVBlank = false;
                _addrLatch = false;
                return value;
            }
            case 4:
                return _oam[_oamAddr];
            case 7:
                return ReadPpuData();
            default:
                return 0; // write-only registers
        }
    }

    public void WriteRegister(int index, byte value)
    {
        switch (index)
        {
            case 0:
                WritePpuCtrl(value);
                break;
            case 1:
                _mask = value;
                break;
            case 3:
                _oamAddr = value;
                break;
            case 4:
                _oam[_oamAddr++] = value;
                break;
            case 5:
                WriteScroll(value);
                break;
            case 6:
                WriteAddr(value);
                break;
            case 7:
                WritePpuData(value);
                break;
        }
    }

    private void WritePpuCtrl(byte value)
    {
        bool nmiWasEnabled = (_ctrl & 0x80) != 0;
        _ctrl = value;
        _tempAddr = (ushort)((_tempAddr & 0xF3FF) | ((value & 0x03) << 10));
        bool nmiNowEnabled = (_ctrl & 0x80) != 0;
        // Enabling NMI while VBlank is already flagged fires immediately (a well-known
        // hardware quirk some games and test ROMs rely on), rather than waiting for the
        // next VBlank.
        if (!nmiWasEnabled && nmiNowEnabled && _statusVBlank)
        {
            _nmiRequested = true;
        }
    }

    private void WriteScroll(byte value)
    {
        if (!_addrLatch)
        {
            _tempAddr = (ushort)((_tempAddr & 0xFFE0) | (value >> 3));
            _fineX = (byte)(value & 0x07);
        }
        else
        {
            _tempAddr = (ushort)((_tempAddr & 0x0C1F) | ((value & 0x07) << 12) | ((value & 0xF8) << 2));
        }
        _addrLatch = !_addrLatch;
    }

    private void WriteAddr(byte value)
    {
        if (!_addrLatch)
        {
            _tempAddr = (ushort)((_tempAddr & 0x00FF) | ((value & 0x3F) << 8));
        }
        else
        {
            _tempAddr = (ushort)((_tempAddr & 0xFF00) | value);
            _vramAddr = _tempAddr;
        }
        _addrLatch = !_addrLatch;
    }

    private byte ReadPpuData()
    {
        byte result;
        if (_vramAddr >= 0x3F00)
        {
            // Palette reads are immediate (no buffering delay); the buffer still gets
            // refreshed with the nametable byte "beneath" the palette mirror, matching
            // real hardware.
            result = PpuBusRead(_vramAddr);
            _dataBuffer = PpuBusRead((ushort)(_vramAddr - 0x1000));
        }
        else
        {
            result = _dataBuffer;
            _dataBuffer = PpuBusRead(_vramAddr);
        }
        IncrementVramAddr();
        return result;
    }

    private void WritePpuData(byte value)
    {
        PpuBusWrite(_vramAddr, value);
        IncrementVramAddr();
    }

    private void IncrementVramAddr()
    {
        int step = (_ctrl & 0x04) != 0 ? 32 : 1;
        _vramAddr = (ushort)((_vramAddr + step) & 0x3FFF);
    }

    // ---- Background fetch pipeline ----

    private void FetchNametableByte()
    {
        _bgNextTileId = PpuBusRead((ushort)(0x2000 | (_vramAddr & 0x0FFF)));
    }

    private void FetchAttributeByte()
    {
        ushort attrAddr = (ushort)(0x23C0 | (_vramAddr & 0x0C00) | ((_vramAddr >> 4) & 0x38) | ((_vramAddr >> 2) & 0x07));
        byte attrByte = PpuBusRead(attrAddr);
        int coarseX = _vramAddr & 0x1F;
        int coarseY = (_vramAddr >> 5) & 0x1F;
        int shift = ((coarseY & 0x02) << 1) | (coarseX & 0x02);
        _bgNextTileAttrib = (byte)((attrByte >> shift) & 0x03);
    }

    private void FetchPatternLowByte()
    {
        ushort addr = BackgroundPatternAddress(0);
        _bgNextTileLsb = PpuBusRead(addr);
    }

    private void FetchPatternHighByte()
    {
        ushort addr = BackgroundPatternAddress(8);
        _bgNextTileMsb = PpuBusRead(addr);
    }

    private ushort BackgroundPatternAddress(int planeOffset)
    {
        int fineY = (_vramAddr >> 12) & 0x07;
        ushort baseAddr = (ushort)((_ctrl & 0x10) != 0 ? 0x1000 : 0x0000);
        return (ushort)(baseAddr + _bgNextTileId * 16 + fineY + planeOffset);
    }

    private void LoadBackgroundShiftersFromLatches()
    {
        _bgShifterPatternLo = (ushort)((_bgShifterPatternLo & 0xFF00) | _bgNextTileLsb);
        _bgShifterPatternHi = (ushort)((_bgShifterPatternHi & 0xFF00) | _bgNextTileMsb);
        _bgShifterAttribLo = (ushort)((_bgShifterAttribLo & 0xFF00) | ((_bgNextTileAttrib & 0b01) != 0 ? 0xFF : 0x00));
        _bgShifterAttribHi = (ushort)((_bgShifterAttribHi & 0xFF00) | ((_bgNextTileAttrib & 0b10) != 0 ? 0xFF : 0x00));
    }

    private void UpdateBackgroundShifters()
    {
        _bgShifterPatternLo <<= 1;
        _bgShifterPatternHi <<= 1;
        _bgShifterAttribLo <<= 1;
        _bgShifterAttribHi <<= 1;
    }

    private void IncrementCoarseX()
    {
        if ((_vramAddr & 0x001F) == 31)
        {
            _vramAddr &= unchecked((ushort)~0x001F);
            _vramAddr ^= 0x0400;
        }
        else
        {
            _vramAddr++;
        }
    }

    private void IncrementY()
    {
        if ((_vramAddr & 0x7000) != 0x7000)
        {
            _vramAddr += 0x1000;
        }
        else
        {
            _vramAddr &= unchecked((ushort)~0x7000);
            int coarseY = (_vramAddr & 0x03E0) >> 5;
            if (coarseY == 29)
            {
                coarseY = 0;
                _vramAddr ^= 0x0800;
            }
            else if (coarseY == 31)
            {
                coarseY = 0;
            }
            else
            {
                coarseY++;
            }
            _vramAddr = (ushort)((_vramAddr & ~0x03E0) | (coarseY << 5));
        }
    }

    private void CopyHorizontalBits() => _vramAddr = (ushort)((_vramAddr & 0xFBE0) | (_tempAddr & 0x041F));

    private void CopyVerticalBits() => _vramAddr = (ushort)((_vramAddr & 0x041F) | (_tempAddr & 0x7BE0));

    // ---- Sprites ----

    private void EvaluateSpritesForNextScanline()
    {
        int targetScanline = Scanline + 1;
        int spriteHeight = (_ctrl & 0x20) != 0 ? 16 : 8;

        _spriteCount = 0;
        int i = 0;
        for (; i < 64 && _spriteCount < MaxSpritesPerScanline; i++)
        {
            int oamBase = i * 4;
            byte spriteY = _oam[oamBase];
            // Real hardware has a one-scanline pipeline delay: a sprite's first visible row
            // is OAM_Y + 1, not OAM_Y (games write Y-1 to compensate — this is documented
            // NES hardware behavior, not a simplification on our part).
            int row = targetScanline - spriteY - 1;
            if (row < 0 || row >= spriteHeight)
            {
                continue;
            }

            byte tileIndex = _oam[oamBase + 1];
            byte attributes = _oam[oamBase + 2];
            byte spriteX = _oam[oamBase + 3];
            bool flipY = (attributes & 0x80) != 0;
            bool flipX = (attributes & 0x40) != 0;

            if (flipY)
            {
                row = spriteHeight - 1 - row;
            }

            ushort patternAddr;
            if (spriteHeight == 16)
            {
                int table = tileIndex & 0x01;
                int tile = tileIndex & 0xFE;
                if (row >= 8)
                {
                    tile += 1;
                    row -= 8;
                }
                patternAddr = (ushort)((table << 12) + tile * 16 + row);
            }
            else
            {
                int table = (_ctrl & 0x08) != 0 ? 1 : 0;
                patternAddr = (ushort)((table << 12) + tileIndex * 16 + row);
            }

            byte lo = PpuBusRead(patternAddr);
            byte hi = PpuBusRead((ushort)(patternAddr + 8));
            if (flipX)
            {
                lo = ReverseBits(lo);
                hi = ReverseBits(hi);
            }

            _spritePatternLo[_spriteCount] = lo;
            _spritePatternHi[_spriteCount] = hi;
            _spriteAttrib[_spriteCount] = attributes;
            _spriteX[_spriteCount] = spriteX;
            _spriteIsZero[_spriteCount] = i == 0;
            _spriteCount++;
        }

        for (; i < 64; i++)
        {
            byte spriteY = _oam[i * 4];
            int row = targetScanline - spriteY - 1;
            if (row >= 0 && row < spriteHeight)
            {
                _statusSpriteOverflow = true;
                break;
            }
        }
    }

    private static byte ReverseBits(byte value)
    {
        byte result = 0;
        for (int bit = 0; bit < 8; bit++)
        {
            result = (byte)((result << 1) | (value & 1));
            value >>= 1;
        }
        return result;
    }

    // ---- Pixel composition ----

    private void RenderPixel(int x, int y)
    {
        int bgPixel = 0, bgPalette = 0;
        if ((_mask & 0x08) != 0 && (x >= 8 || (_mask & 0x02) != 0))
        {
            ushort bitMux = (ushort)(0x8000 >> _fineX);
            int p0 = (_bgShifterPatternLo & bitMux) != 0 ? 1 : 0;
            int p1 = (_bgShifterPatternHi & bitMux) != 0 ? 1 : 0;
            bgPixel = (p1 << 1) | p0;
            int pal0 = (_bgShifterAttribLo & bitMux) != 0 ? 1 : 0;
            int pal1 = (_bgShifterAttribHi & bitMux) != 0 ? 1 : 0;
            bgPalette = (pal1 << 1) | pal0;
        }

        int spritePixel = 0, spritePalette = 0, spritePriorityBehind = 0;
        bool spriteZeroRendered = false;
        if ((_mask & 0x10) != 0 && (x >= 8 || (_mask & 0x04) != 0))
        {
            for (int i = 0; i < _spriteCount; i++)
            {
                int offset = x - _spriteX[i];
                if (offset < 0 || offset > 7)
                {
                    continue;
                }
                int bit = 7 - offset;
                int p0 = (_spritePatternLo[i] >> bit) & 1;
                int p1 = (_spritePatternHi[i] >> bit) & 1;
                int pixel = (p1 << 1) | p0;
                if (pixel != 0 && spritePixel == 0)
                {
                    spritePixel = pixel;
                    spritePalette = (_spriteAttrib[i] & 0x03) + 4;
                    spritePriorityBehind = (_spriteAttrib[i] & 0x20) != 0 ? 1 : 0;
                    spriteZeroRendered = _spriteIsZero[i];
                }
            }
        }

        int finalPixel;
        int finalPalette;
        if (bgPixel == 0 && spritePixel == 0)
        {
            finalPixel = 0;
            finalPalette = 0;
        }
        else if (bgPixel == 0)
        {
            finalPixel = spritePixel;
            finalPalette = spritePalette;
        }
        else if (spritePixel == 0)
        {
            finalPixel = bgPixel;
            finalPalette = bgPalette;
        }
        else
        {
            if (spritePriorityBehind == 0)
            {
                finalPixel = spritePixel;
                finalPalette = spritePalette;
            }
            else
            {
                finalPixel = bgPixel;
                finalPalette = bgPalette;
            }
            if (spriteZeroRendered && x != 255 && (_mask & 0x18) == 0x18)
            {
                _statusSpriteZeroHit = true;
            }
        }

        byte colorIndex = _paletteRam[MirrorPaletteIndex((finalPalette << 2) | finalPixel)];
        Frame[y * 256 + x] = (byte)(colorIndex & 0x3F);
    }

    // ---- PPU bus (VRAM: pattern tables via mapper, nametables, palette) ----

    private byte PpuBusRead(ushort address)
    {
        address &= 0x3FFF;
        _mapper.NotifyA12(address);
        if (address < 0x2000)
        {
            return _mapper.PpuRead(address);
        }
        if (address < 0x3F00)
        {
            return _nametables[MapNametableAddress(address)];
        }
        return _paletteRam[MirrorPaletteIndex(address & 0x1F)];
    }

    private void PpuBusWrite(ushort address, byte value)
    {
        address &= 0x3FFF;
        _mapper.NotifyA12(address);
        if (address < 0x2000)
        {
            _mapper.PpuWrite(address, value);
            return;
        }
        if (address < 0x3F00)
        {
            _nametables[MapNametableAddress(address)] = value;
            return;
        }
        _paletteRam[MirrorPaletteIndex(address & 0x1F)] = value;
    }

    private int MapNametableAddress(ushort address)
    {
        int foldedTo2000Range = (address - 0x2000) & 0x0FFF; // folds $3000-$3EFF onto $2000-$2EFF
        int logicalTable = foldedTo2000Range / 0x400;
        int offsetInTable = foldedTo2000Range % 0x400;
        int physicalTable = _mapper.Mirroring switch
        {
            MirroringMode.Horizontal => logicalTable / 2,
            MirroringMode.Vertical => logicalTable % 2,
            MirroringMode.SingleScreenLower => 0,
            MirroringMode.SingleScreenUpper => 1,
            _ => logicalTable % 2,
        };
        return physicalTable * 0x400 + offsetInTable;
    }

    private static int MirrorPaletteIndex(int index)
    {
        // $3F10/$3F14/$3F18/$3F1C mirror the corresponding background color entry.
        if (index >= 16 && index % 4 == 0)
        {
            return index - 16;
        }
        return index;
    }
}
