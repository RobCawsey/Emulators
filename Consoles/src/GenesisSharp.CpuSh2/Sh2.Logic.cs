namespace GenesisSharp.CpuSh2;

/// <summary>AND/OR/XOR/TST/NOT, plus their R0-immediate and GBR-relative-memory forms.
/// Confirmed line-for-line against PicoDrive's cpu/sh2/mame/sh2.c (citations per method). The
/// GBR-relative memory forms (ANDM/ORM/XORM/TSTM) cost 3 cycles (base 1 + PicoDrive's own
/// "icount -= 2"), reflecting a real byte read-modify-write bus access — the plain
/// register/immediate forms cost 1.</summary>
public sealed partial class Sh2
{
    /// <summary>AND Rm,Rn. cpu/sh2/mame/sh2.c:304-307.</summary>
    private int ExecuteAnd(int m, int n) { R[n] &= R[m]; return 1; }

    /// <summary>AND #imm,R0 — the immediate is used as-is (unsigned, no sign extension), unlike
    /// most other 8-bit-immediate SH-2 instructions. cpu/sh2/mame/sh2.c:314-317.</summary>
    private int ExecuteAndImmediate(int imm8) { R[0] &= (uint)imm8; return 1; }

    /// <summary>AND.B #imm,@(R0,GBR) — read-modify-write against a single byte. cpu/sh2/mame/
    /// sh2.c:323-331.</summary>
    private int ExecuteAndGbr(int imm8)
    {
        uint address = GBR + R[0];
        _bus.WriteByte(address, (byte)(imm8 & _bus.ReadByte(address)));
        return 3;
    }

    /// <summary>OR Rm,Rn. cpu/sh2/mame/sh2.c:1423-1426.</summary>
    private int ExecuteOr(int m, int n) { R[n] |= R[m]; return 1; }

    /// <summary>OR #imm,R0. cpu/sh2/mame/sh2.c:1429-1432.</summary>
    private int ExecuteOrImmediate(int imm8) { R[0] |= (uint)imm8; return 1; }

    /// <summary>OR.B #imm,@(R0,GBR). cpu/sh2/mame/sh2.c:1435-1444.</summary>
    private int ExecuteOrGbr(int imm8)
    {
        uint address = GBR + R[0];
        _bus.WriteByte(address, (byte)(_bus.ReadByte(address) | imm8));
        return 3;
    }

    /// <summary>XOR Rm,Rn. cpu/sh2/mame/sh2.c:1815-1818.</summary>
    private int ExecuteXor(int m, int n) { R[n] ^= R[m]; return 1; }

    /// <summary>XOR #imm,R0. cpu/sh2/mame/sh2.c:1821-1825.</summary>
    private int ExecuteXorImmediate(int imm8) { R[0] ^= (uint)(imm8 & 0xFF); return 1; }

    /// <summary>XOR.B #imm,@(R0,GBR). cpu/sh2/mame/sh2.c:1828-1838.</summary>
    private int ExecuteXorGbr(int imm8)
    {
        uint address = GBR + R[0];
        _bus.WriteByte(address, (byte)(_bus.ReadByte(address) ^ (imm8 & 0xFF)));
        return 3;
    }

    /// <summary>TST Rm,Rn — T = (Rn &amp; Rm) == 0. cpu/sh2/mame/sh2.c:1782-1788.</summary>
    private int ExecuteTst(int m, int n) { FlagT = (R[n] & R[m]) == 0; return 1; }

    /// <summary>TST #imm,R0. cpu/sh2/mame/sh2.c:1791-1799.</summary>
    private int ExecuteTstImmediate(int imm8) { FlagT = ((imm8 & 0xFF) & R[0]) == 0; return 1; }

    /// <summary>TST.B #imm,@(R0,GBR). cpu/sh2/mame/sh2.c:1802-1812.</summary>
    private int ExecuteTstGbr(int imm8)
    {
        FlagT = ((imm8 & 0xFF) & _bus.ReadByte(GBR + R[0])) == 0;
        return 3;
    }

    /// <summary>NOT Rm,Rn. cpu/sh2/mame/sh2.c:1417-1420.</summary>
    private int ExecuteNot(int m, int n) { R[n] = ~R[m]; return 1; }

    /// <summary>CMP/STR Rm,Rn — byte-lane string compare: T is set if *any* of the four byte
    /// lanes of Rn^Rm are zero (i.e. Rn and Rm share at least one equal byte in the same
    /// position), clear only when all four lanes differ. cpu/sh2/mame/sh2.c:560-577.</summary>
    private int ExecuteCmpStr(int m, int n)
    {
        uint temp = R[n] ^ R[m];
        byte hh = (byte)(temp >> 24);
        byte hl = (byte)(temp >> 16);
        byte lh = (byte)(temp >> 8);
        byte ll = (byte)temp;
        FlagT = !(hh != 0 && hl != 0 && lh != 0 && ll != 0);
        return 1;
    }
}
