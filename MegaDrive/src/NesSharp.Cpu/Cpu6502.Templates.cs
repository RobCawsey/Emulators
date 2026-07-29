namespace NesSharp.Cpu;

/// <summary>
/// Addressing-mode cycle templates. Each Build* method enqueues the micro-op steps for the
/// cycles remaining after the opcode fetch, per the real 6502 cycle-by-cycle breakdown for
/// that addressing mode. Indexed modes that can page-cross (AbsoluteX/Y, IndirectY) don't
/// know at decode time whether the extra cycle is needed — the guess-read step decides that
/// at runtime and enqueues the follow-up step only when required, which is what keeps
/// instruction timing exact without precomputing variable-length templates.
/// </summary>
public sealed partial class Cpu6502
{
    private void BuildAddressed(AddressingMode mode, OpKind kind)
    {
        switch (mode)
        {
            case AddressingMode.Immediate: BuildImmediate(); break;
            case AddressingMode.Accumulator: BuildAccumulator(); break;
            case AddressingMode.ZeroPage: BuildZeroPage(kind); break;
            case AddressingMode.ZeroPageX: BuildZeroPageIndexed(kind, useY: false); break;
            case AddressingMode.ZeroPageY: BuildZeroPageIndexed(kind, useY: true); break;
            case AddressingMode.Absolute: BuildAbsolute(kind); break;
            case AddressingMode.AbsoluteX: BuildAbsoluteIndexed(kind, useY: false); break;
            case AddressingMode.AbsoluteY: BuildAbsoluteIndexed(kind, useY: true); break;
            case AddressingMode.IndirectX: BuildIndirectX(kind); break;
            case AddressingMode.IndirectY: BuildIndirectY(kind); break;
            default: throw new NotSupportedException($"{mode} has no generic template.");
        }
    }

    // ---- shared final-stage steps, valid once _effectiveAddr is set ----

    private void StepReadEffectiveAndOperate()
    {
        _fetched = Read(_effectiveAddr);
        _current.ReadOperation!(this, _fetched);
    }

    private void StepWriteFromRegister() => Write(_effectiveAddr, _current.WriteOperation!(this));

    private void StepReadEffective() => _fetched = Read(_effectiveAddr);

    private void StepDummyWriteAndModify()
    {
        Write(_effectiveAddr, _fetched); // R-M-W always writes the old value back first
        _fetched = _current.ModifyOperation!(this, _fetched);
    }

    private void StepWriteFetched() => Write(_effectiveAddr, _fetched);

    private void EnqueueFinal(OpKind kind)
    {
        switch (kind)
        {
            case OpKind.Read:
                _uops.Enqueue(StepReadEffectiveAndOperate);
                break;
            case OpKind.Write:
                _uops.Enqueue(StepWriteFromRegister);
                break;
            case OpKind.Modify:
                _uops.Enqueue(StepReadEffective);
                _uops.Enqueue(StepDummyWriteAndModify);
                _uops.Enqueue(StepWriteFetched);
                break;
        }
    }

    // ---- Immediate (2 cycles) / Accumulator (2 cycles) ----

    private void BuildImmediate() => _uops.Enqueue(StepImmediateReadAndOperate);

    private void StepImmediateReadAndOperate()
    {
        _fetched = Read(PC++);
        _current.ReadOperation!(this, _fetched);
    }

    private void BuildAccumulator() => _uops.Enqueue(StepAccumulatorModify);

    private void StepAccumulatorModify()
    {
        Read(PC); // bus-filler read, PC not advanced — matches real hardware's implied-mode cycle 2
        A = _current.ModifyOperation!(this, A);
    }

    // ---- Implied (2 cycles) ----

    private void BuildImplied() => _uops.Enqueue(StepImpliedExecute);

    private void StepImpliedExecute()
    {
        Read(PC); // bus-filler read, PC not advanced
        _current.ImpliedOperation!(this);
    }

    // ---- Zero Page (3 / 5 cycles) ----

    private void BuildZeroPage(OpKind kind)
    {
        _uops.Enqueue(StepFetchZpAddrDirect);
        EnqueueFinal(kind);
    }

    private void StepFetchZpAddrDirect() => _effectiveAddr = Read(PC++);

    // ---- Zero Page,X / Zero Page,Y (4 / 6 cycles) ----

    private void BuildZeroPageIndexed(OpKind kind, bool useY)
    {
        _usesYIndex = useY;
        _uops.Enqueue(StepFetchZpBase);
        _uops.Enqueue(StepZpIndexDummyRead);
        EnqueueFinal(kind);
    }

    private void StepFetchZpBase() => _addrLo = Read(PC++);

    private void StepZpIndexDummyRead()
    {
        Read(_addrLo); // dummy read at the unindexed zero-page address
        byte idx = _usesYIndex ? Y : X;
        _effectiveAddr = (byte)(_addrLo + idx); // wraps within the zero page, never carries out
    }

    // ---- Absolute (4 / 6 cycles) ----

    private void BuildAbsolute(OpKind kind)
    {
        _uops.Enqueue(StepFetchAddrLo);
        _uops.Enqueue(StepFetchAddrHiAbsolute);
        EnqueueFinal(kind);
    }

    private void StepFetchAddrLo() => _addrLo = Read(PC++);

    private void StepFetchAddrHiAbsolute()
    {
        _addrHi = Read(PC++);
        _effectiveAddr = (ushort)((_addrHi << 8) | _addrLo);
    }

    // ---- Absolute,X / Absolute,Y (Read: 4 or 5; Write: 5; Modify: 7) ----

    private void BuildAbsoluteIndexed(OpKind kind, bool useY)
    {
        _usesYIndex = useY;
        _pendingKindForIndexed = kind;
        _uops.Enqueue(StepFetchAddrLo);
        _uops.Enqueue(StepFetchAddrHiIndexedCalc);
        _uops.Enqueue(StepAbsIndexedGuessRead);
    }

    private void StepFetchAddrHiIndexedCalc()
    {
        _addrHi = Read(PC++);
        ushort baseAddr = (ushort)((_addrHi << 8) | _addrLo);
        byte idx = _usesYIndex ? Y : X;
        _effectiveAddr = (ushort)(baseAddr + idx);
        _guessAddr = (ushort)((_addrHi << 8) | (byte)(_addrLo + idx)); // same page, wrapped low byte
        _pageCrossed = (_effectiveAddr & 0xFF00) != (_guessAddr & 0xFF00);
    }

    private void StepAbsIndexedGuessRead()
    {
        byte guessValue = Read(_guessAddr);
        switch (_pendingKindForIndexed)
        {
            case OpKind.Read when _pageCrossed:
                _uops.Enqueue(StepReadEffectiveAndOperate); // extra cycle: re-read at the corrected address
                break;
            case OpKind.Read:
                _fetched = guessValue; // guess address was already correct — done in 4 cycles
                _current.ReadOperation!(this, _fetched);
                break;
            case OpKind.Write:
                // Write/Modify always take the extra cycle: the guess read is a fixed dummy,
                // regardless of whether the page actually crossed.
                _uops.Enqueue(StepWriteFromRegister);
                break;
            case OpKind.Modify:
                _uops.Enqueue(StepReadEffective);
                _uops.Enqueue(StepDummyWriteAndModify);
                _uops.Enqueue(StepWriteFetched);
                break;
        }
    }

    // ---- (Indirect,X) — 6 cycles, Read or Write only among official opcodes ----

    private void BuildIndirectX(OpKind kind)
    {
        _uops.Enqueue(StepFetchZpPointer);
        _uops.Enqueue(StepIndirectXDummyRead);
        _uops.Enqueue(StepIndirectXFetchLo);
        _uops.Enqueue(StepIndirectXFetchHi);
        EnqueueFinal(kind);
    }

    private void StepFetchZpPointer() => _ptr = Read(PC++);

    private void StepIndirectXDummyRead() => Read(_ptr); // dummy read at the unindexed pointer

    private void StepIndirectXFetchLo() => _addrLo = Read((byte)(_ptr + X));

    private void StepIndirectXFetchHi()
    {
        _addrHi = Read((byte)(_ptr + X + 1)); // pointer add wraps within the zero page
        _effectiveAddr = (ushort)((_addrHi << 8) | _addrLo);
    }

    // ---- (Indirect),Y — Read: 5 or 6; Write: 6; Modify (illegal opcodes only): 8. ----

    private void BuildIndirectY(OpKind kind)
    {
        _pendingKindForIndexed = kind;
        _uops.Enqueue(StepFetchZpPointer);
        _uops.Enqueue(StepIndirectYFetchLo);
        _uops.Enqueue(StepIndirectYFetchHiCalc);
        _uops.Enqueue(StepIndirectYGuessRead);
    }

    private void StepIndirectYFetchLo() => _addrLo = Read(_ptr);

    private void StepIndirectYFetchHiCalc()
    {
        _addrHi = Read((byte)(_ptr + 1)); // wraps within the zero page
        ushort baseAddr = (ushort)((_addrHi << 8) | _addrLo);
        _effectiveAddr = (ushort)(baseAddr + Y);
        _guessAddr = (ushort)((_addrHi << 8) | (byte)(_addrLo + Y));
        _pageCrossed = (_effectiveAddr & 0xFF00) != (_guessAddr & 0xFF00);
    }

    private void StepIndirectYGuessRead()
    {
        byte guessValue = Read(_guessAddr);
        switch (_pendingKindForIndexed)
        {
            case OpKind.Read when _pageCrossed:
                _uops.Enqueue(StepReadEffectiveAndOperate);
                break;
            case OpKind.Read:
                _fetched = guessValue;
                _current.ReadOperation!(this, _fetched);
                break;
            case OpKind.Write:
                // STA (Indirect),Y always takes the extra cycle, cross or not.
                _uops.Enqueue(StepWriteFromRegister);
                break;
            case OpKind.Modify:
                // Illegal opcodes only (DCP/ISC/SLO/RLA/SRE/RRA (Indirect),Y) — always 8 cycles.
                _uops.Enqueue(StepReadEffective);
                _uops.Enqueue(StepDummyWriteAndModify);
                _uops.Enqueue(StepWriteFetched);
                break;
        }
    }

    // ---- Relative branches (2 / 3 / 4 cycles) ----

    private void BuildBranch() => _uops.Enqueue(StepBranchFetchOffsetAndDecide);

    private void StepBranchFetchOffsetAndDecide()
    {
        sbyte offset = (sbyte)Read(PC++);
        if (!_current.Branch!(this))
        {
            return; // not taken: done in 2 cycles
        }

        ushort pcNext = PC;
        _effectiveAddr = (ushort)(pcNext + offset);
        _pageCrossed = (_effectiveAddr & 0xFF00) != (pcNext & 0xFF00);
        _uops.Enqueue(StepBranchTakenDummy);
    }

    private void StepBranchTakenDummy()
    {
        // Real hardware's dummy read here targets the not-yet-page-corrected address; we read
        // the final target instead. Harmless on a flat bus, and no real NES program relies on
        // a branch's dummy read hitting a memory-mapped register mid-branch, so cycle *count*
        // stays exact (2/3/4) while the dummy read's address is a deliberate simplification.
        Read(_effectiveAddr);
        if (_pageCrossed)
        {
            _uops.Enqueue(StepBranchPageFix);
        }
        else
        {
            PC = _effectiveAddr;
        }
    }

    private void StepBranchPageFix()
    {
        Read(_effectiveAddr);
        PC = _effectiveAddr;
    }
}
