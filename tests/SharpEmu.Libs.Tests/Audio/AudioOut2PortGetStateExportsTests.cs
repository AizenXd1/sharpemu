// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Audio;
using Xunit;

namespace SharpEmu.Libs.Tests.Audio;

public sealed class AudioOut2PortGetStateExportsTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong StateAddress = MemoryBase + 0x100;

    private static CpuContext CreateContext(out FakeCpuMemory memory)
    {
        memory = new FakeCpuMemory(MemoryBase, 0x1000);
        return new CpuContext(memory, Generation.Gen5);
    }

    [Fact]
    public void PortGetState_WritesFixedSizeIgnoringPollutedR9()
    {
        var ctx = CreateContext(out var memory);
        // Paint the buffer so we can see the write footprint.
        Span<byte> paint = stackalloc byte[0x100];
        paint.Fill(0xAB);
        Assert.True(memory.TryWrite(StateAddress, paint));

        ctx[CpuRegister.Rdi] = 0xDE1FF6800001UL;
        ctx[CpuRegister.Rsi] = StateAddress;
        ctx[CpuRegister.Rdx] = StateAddress + 0x200;
        // Polluted GetSize leftover — must NOT enlarge the write.
        ctx[CpuRegister.R9] = 0x180;

        var result = AudioOut2Exports.AudioOut2PortGetState(ctx);

        Assert.Equal(0, result);
        Span<byte> state = stackalloc byte[0x100];
        Assert.True(memory.TryRead(StateAddress, state));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(state));
        Assert.Equal(2, state[2]);
        // Bytes past the fixed 0x20 header must remain untouched.
        Assert.Equal(0xAB, state[0x20]);
        Assert.Equal(0xAB, state[0x7F]);
    }

    [Fact]
    public void PortGetState_SkipsGuestStackOutBuffer()
    {
        var ctx = CreateContext(out _);
        const ulong stackOut = 0x00007FFFDE1FF688UL;
        ctx[CpuRegister.Rdi] = 0xDE1FF688004DUL;
        ctx[CpuRegister.Rsi] = stackOut;
        ctx[CpuRegister.Rdx] = 0;

        var result = AudioOut2Exports.AudioOut2PortGetState(ctx);

        Assert.Equal(0, result);
    }

    [Fact]
    public void GetSpeakerInfo_WritesFixedSizeToRdiNotRsiTypeFlag()
    {
        var ctx = CreateContext(out var memory);
        Span<byte> paint = stackalloc byte[0x80];
        paint.Fill(0xCD);
        Assert.True(memory.TryWrite(StateAddress, paint));

        ctx[CpuRegister.Rdi] = StateAddress;
        ctx[CpuRegister.Rsi] = 1;
        ctx[CpuRegister.Rdx] = StateAddress + 0x200;
        ctx[CpuRegister.R8] = 0x840;
        ctx[CpuRegister.R9] = 0x10C;

        var result = AudioOut2Exports.AudioOut2GetSpeakerInfo(ctx);

        Assert.Equal(0, result);
        Span<byte> info = stackalloc byte[0x80];
        Assert.True(memory.TryRead(StateAddress, info));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(info));
        Assert.Equal(48000u, BinaryPrimitives.ReadUInt32LittleEndian(info[4..]));
        Assert.Equal(0xCD, info[0x20]);
    }

    [Fact]
    public void ContextGetQueueLevel_ReportsGrainsQueuedByAdvanceAndPush()
    {
        // Wwise renders exactly puiAvailableQueues grains per update and pushes them
        // non-blocking; a queue that never fills made it block in Push under its lock.
        var ctx = CreateContext(out var memory);
        var param = new byte[0x40];
        BinaryPrimitives.WriteUInt32LittleEndian(param.AsSpan(0x0C), 4);
        // 16384-sample grains (341 ms) keep the queue from draining during the test.
        BinaryPrimitives.WriteUInt32LittleEndian(param.AsSpan(0x10), 0x4000);
        Assert.True(memory.TryWrite(StateAddress, param));
        ctx[CpuRegister.Rdi] = StateAddress;
        ctx[CpuRegister.Rsi] = StateAddress + 0x100;
        ctx[CpuRegister.Rdx] = 0x1000;
        ctx[CpuRegister.Rcx] = StateAddress + 0x200;
        Assert.Equal(0, AudioOut2Exports.AudioOut2ContextCreate(ctx));
        Span<byte> handleBytes = stackalloc byte[8];
        Assert.True(memory.TryRead(StateAddress + 0x200, handleBytes));
        var handle = BinaryPrimitives.ReadUInt64LittleEndian(handleBytes);

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        Span<byte> level = stackalloc byte[0x0C];
        for (var grain = 1u; grain <= 4; grain++)
        {
            ctx[CpuRegister.Rdi] = handle;
            AudioOut2Exports.AudioOut2ContextAdvance(ctx);
            ctx[CpuRegister.Rdi] = handle;
            ctx[CpuRegister.Rsi] = 0;
            AudioOut2Exports.AudioOut2ContextPush(ctx);

            ctx[CpuRegister.Rdi] = handle;
            ctx[CpuRegister.Rsi] = StateAddress + 0x300;
            ctx[CpuRegister.Rdx] = StateAddress + 0x308;
            Assert.Equal(0, AudioOut2Exports.AudioOut2ContextGetQueueLevel(ctx));
            Assert.True(memory.TryRead(StateAddress + 0x300, level));
            Assert.Equal(grain, BinaryPrimitives.ReadUInt32LittleEndian(level));
            Assert.Equal(4 - grain, BinaryPrimitives.ReadUInt32LittleEndian(level[8..]));
        }

        // Filling the queue up to its depth never blocks.
        Assert.True(System.Diagnostics.Stopwatch.GetElapsedTime(started) < TimeSpan.FromMilliseconds(300));
        ctx[CpuRegister.Rdi] = handle;
        AudioOut2Exports.AudioOut2ContextDestroy(ctx);
    }
}
