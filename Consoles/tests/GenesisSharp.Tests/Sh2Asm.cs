namespace GenesisSharp.Tests;

/// <summary>Hand-assembles the SH-2 opcodes this core currently implements, straight from the
/// bit-field formulas confirmed against PicoDrive's dispatch (cpu/sh2/mame/sh2.c/sh2pico.c) —
/// much easier to check for mistakes than hand-computed hex, and mirrors this test project's
/// existing Asm.cs/Z80Asm.cs convention.</summary>
internal static class Sh2Asm
{
    public static ushort Nop() => 0x0009;
    public static ushort ClrT() => 0x0008;
    public static ushort SetT() => 0x0018;
    public static ushort Div0u() => 0x0019;
    public static ushort Sleep() => 0x001B;
    public static ushort Clrmac() => 0x0028;
    public static ushort Rts() => 0x000B;
    public static ushort Rte() => 0x002B;
    public static ushort StcSr(int n) => (ushort)(0x0002 | (n << 8));
    public static ushort StcGbr(int n) => (ushort)(0x0012 | (n << 8));
    public static ushort StcVbr(int n) => (ushort)(0x0022 | (n << 8));
    public static ushort StsMach(int n) => (ushort)(0x000A | (n << 8));
    public static ushort StsMacl(int n) => (ushort)(0x001A | (n << 8));
    public static ushort StsPr(int n) => (ushort)(0x002A | (n << 8));
    public static ushort Bsrf(int m) => (ushort)(0x0003 | (m << 8));
    public static ushort Braf(int m) => (ushort)(0x0023 | (m << 8));
    public static ushort Movt(int n) => (ushort)(0x0029 | (n << 8));

    public static ushort MovBS0(int m, int n) => (ushort)(0x0004 | (n << 8) | (m << 4));
    public static ushort MovWS0(int m, int n) => (ushort)(0x0005 | (n << 8) | (m << 4));
    public static ushort MovLS0(int m, int n) => (ushort)(0x0006 | (n << 8) | (m << 4));
    public static ushort MovBL0(int m, int n) => (ushort)(0x000C | (n << 8) | (m << 4));
    public static ushort MovWL0(int m, int n) => (ushort)(0x000D | (n << 8) | (m << 4));
    public static ushort MovLL0(int m, int n) => (ushort)(0x000E | (n << 8) | (m << 4));
    public static ushort Mull(int m, int n) => (ushort)(0x0007 | (n << 8) | (m << 4));
    public static ushort MacL(int m, int n) => (ushort)(0x000F | (n << 8) | (m << 4));

    public static ushort MovLS4(int m, int disp4, int n) => (ushort)(0x1000 | (n << 8) | (m << 4) | (disp4 & 0xF));

    public static ushort MovBS(int m, int n) => (ushort)(0x2000 | (n << 8) | (m << 4) | 0x0);
    public static ushort MovWS(int m, int n) => (ushort)(0x2000 | (n << 8) | (m << 4) | 0x1);
    public static ushort MovLS(int m, int n) => (ushort)(0x2000 | (n << 8) | (m << 4) | 0x2);
    public static ushort MovBM(int m, int n) => (ushort)(0x2000 | (n << 8) | (m << 4) | 0x4);
    public static ushort MovWM(int m, int n) => (ushort)(0x2000 | (n << 8) | (m << 4) | 0x5);
    public static ushort MovLM(int m, int n) => (ushort)(0x2000 | (n << 8) | (m << 4) | 0x6);
    public static ushort Tst(int m, int n) => (ushort)(0x2000 | (n << 8) | (m << 4) | 0x8);
    public static ushort And(int m, int n) => (ushort)(0x2000 | (n << 8) | (m << 4) | 0x9);
    public static ushort Xor(int m, int n) => (ushort)(0x2000 | (n << 8) | (m << 4) | 0xA);
    public static ushort Or(int m, int n) => (ushort)(0x2000 | (n << 8) | (m << 4) | 0xB);
    public static ushort Div0s(int m, int n) => (ushort)(0x2000 | (n << 8) | (m << 4) | 0x7);
    public static ushort CmpStr(int m, int n) => (ushort)(0x2000 | (n << 8) | (m << 4) | 0xC);
    public static ushort Xtrct(int m, int n) => (ushort)(0x2000 | (n << 8) | (m << 4) | 0xD);
    public static ushort Mulu(int m, int n) => (ushort)(0x2000 | (n << 8) | (m << 4) | 0xE);
    public static ushort Muls(int m, int n) => (ushort)(0x2000 | (n << 8) | (m << 4) | 0xF);

    public static ushort CmpEq(int m, int n) => (ushort)(0x3000 | (n << 8) | (m << 4) | 0x0);
    public static ushort CmpHs(int m, int n) => (ushort)(0x3000 | (n << 8) | (m << 4) | 0x2);
    public static ushort CmpGe(int m, int n) => (ushort)(0x3000 | (n << 8) | (m << 4) | 0x3);
    public static ushort Div1(int m, int n) => (ushort)(0x3000 | (n << 8) | (m << 4) | 0x4);
    public static ushort Dmulu(int m, int n) => (ushort)(0x3000 | (n << 8) | (m << 4) | 0x5);
    public static ushort CmpHi(int m, int n) => (ushort)(0x3000 | (n << 8) | (m << 4) | 0x6);
    public static ushort CmpGt(int m, int n) => (ushort)(0x3000 | (n << 8) | (m << 4) | 0x7);
    public static ushort Sub(int m, int n) => (ushort)(0x3000 | (n << 8) | (m << 4) | 0x8);
    public static ushort Subc(int m, int n) => (ushort)(0x3000 | (n << 8) | (m << 4) | 0xA);
    public static ushort Subv(int m, int n) => (ushort)(0x3000 | (n << 8) | (m << 4) | 0xB);
    public static ushort Add(int m, int n) => (ushort)(0x3000 | (n << 8) | (m << 4) | 0xC);
    public static ushort Dmuls(int m, int n) => (ushort)(0x3000 | (n << 8) | (m << 4) | 0xD);
    public static ushort Addc(int m, int n) => (ushort)(0x3000 | (n << 8) | (m << 4) | 0xE);
    public static ushort Addv(int m, int n) => (ushort)(0x3000 | (n << 8) | (m << 4) | 0xF);

    public static ushort Shll(int n) => (ushort)(0x4000 | (n << 8) | 0x00);
    public static ushort Shlr(int n) => (ushort)(0x4000 | (n << 8) | 0x01);
    public static ushort Rotl(int n) => (ushort)(0x4000 | (n << 8) | 0x04);
    public static ushort Rotr(int n) => (ushort)(0x4000 | (n << 8) | 0x05);
    public static ushort Jsr(int n) => (ushort)(0x400B | (n << 8));
    public static ushort Shll2(int n) => (ushort)(0x4000 | (n << 8) | 0x08);
    public static ushort Shlr2(int n) => (ushort)(0x4000 | (n << 8) | 0x09);
    public static ushort Dt(int n) => (ushort)(0x4010 | (n << 8));
    public static ushort CmpPz(int n) => (ushort)(0x4011 | (n << 8));
    public static ushort CmpPl(int n) => (ushort)(0x4015 | (n << 8));
    public static ushort Shll8(int n) => (ushort)(0x4000 | (n << 8) | 0x18);
    public static ushort Shlr8(int n) => (ushort)(0x4000 | (n << 8) | 0x19);
    public static ushort Shal(int n) => (ushort)(0x4000 | (n << 8) | 0x20);
    public static ushort Shar(int n) => (ushort)(0x4000 | (n << 8) | 0x21);
    public static ushort Rotcl(int n) => (ushort)(0x4000 | (n << 8) | 0x24);
    public static ushort Rotcr(int n) => (ushort)(0x4000 | (n << 8) | 0x25);
    public static ushort Shll16(int n) => (ushort)(0x4000 | (n << 8) | 0x28);
    public static ushort Shlr16(int n) => (ushort)(0x4000 | (n << 8) | 0x29);
    public static ushort Jmp(int n) => (ushort)(0x402B | (n << 8));
    public static ushort MacW(int m, int n) => (ushort)(0x400F | (n << 8) | (m << 4));
    public static ushort StsMMach(int n) => (ushort)(0x4002 | (n << 8));
    public static ushort StcMSr(int n) => (ushort)(0x4003 | (n << 8));
    public static ushort LdsMMach(int n) => (ushort)(0x4006 | (n << 8));
    public static ushort LdcMSr(int n) => (ushort)(0x4007 | (n << 8));
    public static ushort LdsMach(int n) => (ushort)(0x400A | (n << 8));
    public static ushort LdcSr(int n) => (ushort)(0x400E | (n << 8));
    public static ushort StsMMacl(int n) => (ushort)(0x4012 | (n << 8));
    public static ushort StcMGbr(int n) => (ushort)(0x4013 | (n << 8));
    public static ushort LdsMMacl(int n) => (ushort)(0x4016 | (n << 8));
    public static ushort LdcMGbr(int n) => (ushort)(0x4017 | (n << 8));
    public static ushort LdsMacl(int n) => (ushort)(0x401A | (n << 8));
    public static ushort Tas(int n) => (ushort)(0x401B | (n << 8));
    public static ushort LdcGbr(int n) => (ushort)(0x401E | (n << 8));
    public static ushort StsMPr(int n) => (ushort)(0x4022 | (n << 8));
    public static ushort StcMVbr(int n) => (ushort)(0x4023 | (n << 8));
    public static ushort LdsMPr(int n) => (ushort)(0x4026 | (n << 8));
    public static ushort LdcMVbr(int n) => (ushort)(0x4027 | (n << 8));
    public static ushort LdsPr(int n) => (ushort)(0x402A | (n << 8));
    public static ushort LdcVbr(int n) => (ushort)(0x402E | (n << 8));

    public static ushort MovLL4(int m, int disp4, int n) => (ushort)(0x5000 | (n << 8) | (m << 4) | (disp4 & 0xF));

    public static ushort MovBL(int m, int n) => (ushort)(0x6000 | (n << 8) | (m << 4) | 0x0);
    public static ushort MovWL(int m, int n) => (ushort)(0x6000 | (n << 8) | (m << 4) | 0x1);
    public static ushort MovLL(int m, int n) => (ushort)(0x6000 | (n << 8) | (m << 4) | 0x2);
    public static ushort Mov(int m, int n) => (ushort)(0x6000 | (n << 8) | (m << 4) | 0x3);
    public static ushort MovBP(int m, int n) => (ushort)(0x6000 | (n << 8) | (m << 4) | 0x4);
    public static ushort MovWP(int m, int n) => (ushort)(0x6000 | (n << 8) | (m << 4) | 0x5);
    public static ushort MovLP(int m, int n) => (ushort)(0x6000 | (n << 8) | (m << 4) | 0x6);
    public static ushort Not(int m, int n) => (ushort)(0x6000 | (n << 8) | (m << 4) | 0x7);
    public static ushort SwapB(int m, int n) => (ushort)(0x6000 | (n << 8) | (m << 4) | 0x8);
    public static ushort SwapW(int m, int n) => (ushort)(0x6000 | (n << 8) | (m << 4) | 0x9);
    public static ushort Negc(int m, int n) => (ushort)(0x6000 | (n << 8) | (m << 4) | 0xA);
    public static ushort Neg(int m, int n) => (ushort)(0x6000 | (n << 8) | (m << 4) | 0xB);
    public static ushort ExtuB(int m, int n) => (ushort)(0x6000 | (n << 8) | (m << 4) | 0xC);
    public static ushort ExtuW(int m, int n) => (ushort)(0x6000 | (n << 8) | (m << 4) | 0xD);
    public static ushort ExtsB(int m, int n) => (ushort)(0x6000 | (n << 8) | (m << 4) | 0xE);
    public static ushort ExtsW(int m, int n) => (ushort)(0x6000 | (n << 8) | (m << 4) | 0xF);

    public static ushort AddI(int imm8, int n) => (ushort)(0x7000 | (n << 8) | (imm8 & 0xFF));

    public static ushort MovBS4(int disp4, int m) => (ushort)(0x8000 | (0 << 8) | (m << 4) | (disp4 & 0xF));
    public static ushort MovWS4(int disp4, int m) => (ushort)(0x8000 | (1 << 8) | (m << 4) | (disp4 & 0xF));
    public static ushort MovBL4(int m, int disp4) => (ushort)(0x8000 | (4 << 8) | (m << 4) | (disp4 & 0xF));
    public static ushort MovWL4(int m, int disp4) => (ushort)(0x8000 | (5 << 8) | (m << 4) | (disp4 & 0xF));
    public static ushort CmpIm(int imm8) => (ushort)(0x8800 | (imm8 & 0xFF));
    public static ushort Bt(int disp8) => (ushort)(0x8900 | (disp8 & 0xFF));
    public static ushort Bf(int disp8) => (ushort)(0x8B00 | (disp8 & 0xFF));
    public static ushort Bts(int disp8) => (ushort)(0x8D00 | (disp8 & 0xFF));
    public static ushort Bfs(int disp8) => (ushort)(0x8F00 | (disp8 & 0xFF));

    public static ushort MovWI(int disp8, int n) => (ushort)(0x9000 | (n << 8) | (disp8 & 0xFF));
    public static ushort Bra(int disp12) => (ushort)(0xA000 | (disp12 & 0xFFF));
    public static ushort Bsr(int disp12) => (ushort)(0xB000 | (disp12 & 0xFFF));

    public static ushort MovBSG(int disp8) => (ushort)(0xC000 | (disp8 & 0xFF));
    public static ushort MovWSG(int disp8) => (ushort)(0xC100 | (disp8 & 0xFF));
    public static ushort MovLSG(int disp8) => (ushort)(0xC200 | (disp8 & 0xFF));
    public static ushort Trapa(int imm8) => (ushort)(0xC300 | (imm8 & 0xFF));
    public static ushort MovBLG(int disp8) => (ushort)(0xC400 | (disp8 & 0xFF));
    public static ushort MovWLG(int disp8) => (ushort)(0xC500 | (disp8 & 0xFF));
    public static ushort MovLLG(int disp8) => (ushort)(0xC600 | (disp8 & 0xFF));
    public static ushort Mova(int disp8) => (ushort)(0xC700 | (disp8 & 0xFF));
    public static ushort TstI(int imm8) => (ushort)(0xC800 | (imm8 & 0xFF));
    public static ushort AndI(int imm8) => (ushort)(0xC900 | (imm8 & 0xFF));
    public static ushort XorI(int imm8) => (ushort)(0xCA00 | (imm8 & 0xFF));
    public static ushort OrI(int imm8) => (ushort)(0xCB00 | (imm8 & 0xFF));
    public static ushort TstM(int imm8) => (ushort)(0xCC00 | (imm8 & 0xFF));
    public static ushort AndM(int imm8) => (ushort)(0xCD00 | (imm8 & 0xFF));
    public static ushort XorM(int imm8) => (ushort)(0xCE00 | (imm8 & 0xFF));
    public static ushort OrM(int imm8) => (ushort)(0xCF00 | (imm8 & 0xFF));

    public static ushort MovLI(int disp8, int n) => (ushort)(0xD000 | (n << 8) | (disp8 & 0xFF));
    public static ushort MovI(int imm8, int n) => (ushort)(0xE000 | (n << 8) | (imm8 & 0xFF));
}
