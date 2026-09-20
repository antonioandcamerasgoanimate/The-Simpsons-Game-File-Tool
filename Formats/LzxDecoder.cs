using System.Collections.Generic;
using System.IO;
namespace TSGFileTool.Formats;

/// <summary>
/// C# port of libmspack's LZX decoder (Stuart Caie, LGPL 2.1), from the
/// exact source the user supplied (Compression.zip: lzx/mspack/lzxd.c,
/// readbits.h, readhuff.h, lzx.h) - the Gildor-adapted variant used for
/// Xbox 360 XMemCompress/XMemDecompress data (this is the actual algorithm
/// xcompress.dll implements). This is a line-by-line-faithful translation
/// of the C macros/state machine into C# fields and methods - not a
/// reimplementation from a spec - specifically so its bit-level behavior
/// matches the original.
///
/// NOTE ON SCOPE: this decoder is real, tested against the reference source,
/// and used by the STR/Raw tabs' "Try LZX Decompress" actions as a pure
/// managed alternative to the xcompress.dll P/Invoke path (works without
/// Windows/x86). It is NOT proven to decode this game's "EARS" sub-file
/// payloads - extensive testing (every byte offset 0-400, with and without
/// Xbox block framing) against a real EARS-compressed .itxd did not produce
/// valid output; see chat history for that investigation. Genuine Xbox
/// LZX/XMemCompress data (which some resources in this game, or other
/// Xbox 360 titles, may still use) should decode correctly with this class.
/// </summary>
public sealed class LzxDecoder
{
    // ---- constants (mspack/lzx.h) ----
    private const int MIN_MATCH = 2;
    private const int NUM_CHARS = 256;
    private const int BLOCKTYPE_INVALID = 0;
    private const int BLOCKTYPE_VERBATIM = 1;
    private const int BLOCKTYPE_ALIGNED = 2;
    private const int BLOCKTYPE_UNCOMPRESSED = 3;
    private const int NUM_PRIMARY_LENGTHS = 7;
    private const int NUM_SECONDARY_LENGTHS = 249;

    private const int PRETREE_MAXSYMBOLS = 20;
    private const int PRETREE_TABLEBITS = 6;
    private const int MAINTREE_MAXSYMBOLS = NUM_CHARS + 50 * 8;
    private const int MAINTREE_TABLEBITS = 12;
    private const int LENGTH_MAXSYMBOLS = NUM_SECONDARY_LENGTHS + 1;
    private const int LENGTH_TABLEBITS = 12;
    private const int ALIGNED_MAXSYMBOLS = 8;
    private const int ALIGNED_TABLEBITS = 7;
    private const int LENTABLE_SAFETY = 64;
    private const int FRAME_SIZE = 32768;
    private const int HUFF_MAXBITS = 16;

    private static readonly uint[] PositionBase =
    {
        0, 1, 2, 3, 4, 6, 8, 12, 16, 24, 32, 48, 64, 96, 128, 192, 256,
        384, 512, 768, 1024, 1536, 2048, 3072, 4096, 6144, 8192, 12288,
        16384, 24576, 32768, 49152, 65536, 98304, 131072, 196608, 262144,
        393216, 524288, 655360, 786432, 917504, 1048576, 1179648, 1310720,
        1441792, 1572864, 1703936, 1835008, 1966080, 2097152
    };
    private static readonly byte[] ExtraBits =
    {
        0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8,
        9, 9, 10, 10, 11, 11, 12, 12, 13, 13, 14, 14, 15, 15, 16, 16,
        17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17
    };

    private readonly byte[] _in;
    private int _inPos;
    private readonly bool _xboxFraming;
    private int _blockRest;

    private readonly byte[] _window;
    private uint _windowSize, _windowPosn, _framePosn, _frame;
    private readonly int _resetInterval;
    private uint _r0 = 1, _r1 = 1, _r2 = 1;
    private uint _blockLength, _blockRemaining;
    private int _intelFilesize, _intelCurpos;
    private bool _intelStarted;
    private int _blockType = BLOCKTYPE_INVALID;
    private bool _headerRead;
    private int _posnSlots;
    private long _offset;
    private long _length;

    private uint _bitBuffer;
    private int _bitsLeft;

    private readonly byte[] _pretreeLen = new byte[PRETREE_MAXSYMBOLS + LENTABLE_SAFETY];
    private readonly byte[] _maintreeLen = new byte[MAINTREE_MAXSYMBOLS + LENTABLE_SAFETY];
    private readonly byte[] _lengthLen = new byte[LENGTH_MAXSYMBOLS + LENTABLE_SAFETY];
    private readonly byte[] _alignedLen = new byte[ALIGNED_MAXSYMBOLS + LENTABLE_SAFETY];

    private readonly ushort[] _pretreeTable = new ushort[(1 << PRETREE_TABLEBITS) + PRETREE_MAXSYMBOLS * 2];
    private readonly ushort[] _maintreeTable = new ushort[(1 << MAINTREE_TABLEBITS) + MAINTREE_MAXSYMBOLS * 2];
    private readonly ushort[] _lengthTable = new ushort[(1 << LENGTH_TABLEBITS) + LENGTH_MAXSYMBOLS * 2];
    private readonly ushort[] _alignedTable = new ushort[(1 << ALIGNED_TABLEBITS) + ALIGNED_MAXSYMBOLS * 2];
    private bool _lengthEmpty;

    private readonly List<byte> _output = new();

    public string? Error { get; private set; }

    private LzxDecoder(byte[] input, int windowBits, int resetInterval, bool xboxFraming)
    {
        _in = input;
        _resetInterval = resetInterval;
        _xboxFraming = xboxFraming;
        _windowSize = 1u << windowBits;
        _window = new byte[_windowSize];
        _posnSlots = windowBits == 21 ? 50 : windowBits == 20 ? 42 : windowBits << 1;
        ResetState();
    }

    /// <summary>
    /// Decompresses Xbox-360-framed LZX data (the framing xcompress.dll /
    /// XMemDecompress uses: each sub-block starts with either a 2-byte
    /// big-endian compressed-size, or - if the first byte is 0xFF - a
    /// 5-byte header of 0xFF + 2-byte uncompressed size + 2-byte compressed
    /// size). windowBits is normally 17 (128KB window) for Xbox 360 data.
    /// </summary>
    public static byte[]? DecompressXbox(byte[] compressed, int decompressedSize, out string? error, int windowBits = 17)
    {
        var d = new LzxDecoder(compressed, windowBits, 0, xboxFraming: true);
        var result = d.Run(decompressedSize);
        error = d.Error;
        return result;
    }

    /// <summary>Decompresses a raw, unframed LZX bitstream (no Xbox block headers) - e.g. CAB-style.</summary>
    public static byte[]? DecompressRaw(byte[] compressed, int decompressedSize, out string? error, int windowBits = 17, int resetInterval = 0)
    {
        var d = new LzxDecoder(compressed, windowBits, resetInterval, xboxFraming: false);
        var result = d.Run(decompressedSize);
        error = d.Error;
        return result;
    }

    private byte[]? Run(int outBytes)
    {
        try
        {
            int err = Decompress(outBytes);
            if (err != 0) { Error ??= $"LZX error after {_output.Count} of {outBytes} bytes"; return null; }
            return _output.ToArray();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            return null;
        }
    }

    private void ResetState()
    {
        _r0 = _r1 = _r2 = 1;
        _headerRead = false;
        _blockRemaining = 0;
        _blockType = BLOCKTYPE_INVALID;
        Array.Clear(_maintreeLen, 0, MAINTREE_MAXSYMBOLS);
        Array.Clear(_lengthLen, 0, LENGTH_MAXSYMBOLS);
    }

    private bool ReadInput(byte[] buf, out int count)
    {
        count = 0;
        if (!_xboxFraming)
        {
            int avail = _in.Length - _inPos;
            if (avail <= 0) return false;
            count = Math.Min(buf.Length, avail);
            Array.Copy(_in, _inPos, buf, 0, count);
            _inPos += count;
            return true;
        }

        if (_blockRest == 0)
        {
            if (_inPos >= _in.Length) return false;
            if (_in[_inPos] == 0xFF)
            {
                if (_inPos + 5 > _in.Length) return false;
                _blockRest = (_in[_inPos + 3] << 8) | _in[_inPos + 4];
                _inPos += 5;
            }
            else
            {
                if (_inPos + 2 > _in.Length) return false;
                _blockRest = (_in[_inPos + 0] << 8) | _in[_inPos + 1];
                _inPos += 2;
            }
            if (_blockRest > _in.Length - _inPos) _blockRest = _in.Length - _inPos;
        }
        int want = Math.Min(buf.Length, _blockRest);
        if (want <= 0) return false;
        Array.Copy(_in, _inPos, buf, 0, want);
        _inPos += want;
        _blockRest -= want;
        count = want;
        return true;
    }

    private byte[] _stage = Array.Empty<byte>();
    private int _stagePos, _stageEnd;
    private bool _inputEnded;

    private bool FillStage()
    {
        if (_stagePos < _stageEnd) return true;
        _stage = new byte[4096];
        if (!ReadInput(_stage, out int got) || got == 0)
        {
            if (_inputEnded) return false;
            _stage[0] = 0; _stage[1] = 0;
            got = 2;
            _inputEnded = true;
        }
        _stagePos = 0; _stageEnd = got;
        return true;
    }

    private byte NextByte()
    {
        if (_stagePos >= _stageEnd) { if (!FillStage()) throw new InvalidDataException("LZX: out of input"); }
        return _stage[_stagePos++];
    }

    private void EnsureBits(int nbits)
    {
        while (_bitsLeft < nbits)
        {
            byte b0 = NextByte();
            byte b1 = NextByte();
            uint word = (uint)((b1 << 8) | b0);
            _bitBuffer |= word << (32 - 16 - _bitsLeft);
            _bitsLeft += 16;
        }
    }
    private uint PeekBits(int nbits) => _bitBuffer >> (32 - nbits);
    private void RemoveBits(int nbits) { _bitBuffer <<= nbits; _bitsLeft -= nbits; }
    private uint ReadBits(int nbits) { EnsureBits(nbits); uint v = PeekBits(nbits); RemoveBits(nbits); return v; }

    private static bool MakeDecodeTable(int nsyms, int nbits, byte[] length, ushort[] table)
    {
        int pos = 0;
        int tableMask = 1 << nbits;
        int bitMask = tableMask >> 1;

        for (int bitNum = 1; bitNum <= nbits; bitNum++)
        {
            for (int sym = 0; sym < nsyms; sym++)
            {
                if (length[sym] != bitNum) continue;
                int leaf = pos;
                if ((pos += bitMask) > tableMask) return true;
                for (int fill = bitMask; fill-- > 0;) table[leaf++] = (ushort)sym;
            }
            bitMask >>= 1;
        }

        if (pos == tableMask) return false;

        for (int sym = pos; sym < tableMask; sym++) table[sym] = 0xFFFF;

        int nextSymbol = (tableMask >> 1) < nsyms ? nsyms : tableMask >> 1;
        long posL = (long)pos << 16;
        long tableMaskL = (long)tableMask << 16;
        int bitMaskL = 1 << 15;

        for (int bitNum = nbits + 1; bitNum <= HUFF_MAXBITS; bitNum++)
        {
            for (int sym = 0; sym < nsyms; sym++)
            {
                if (length[sym] != bitNum) continue;
                int leaf = (int)(posL >> 16);
                for (int fill = 0; fill < bitNum - nbits; fill++)
                {
                    if (table[leaf] == 0xFFFF)
                    {
                        table[nextSymbol << 1] = 0xFFFF;
                        table[(nextSymbol << 1) + 1] = 0xFFFF;
                        table[leaf] = (ushort)nextSymbol++;
                    }
                    leaf = table[leaf] << 1;
                    if (((posL >> (15 - fill)) & 1) != 0) leaf++;
                }
                table[leaf] = (ushort)sym;
                posL += bitMaskL;
                if (posL > tableMaskL) return true;
            }
            bitMaskL >>= 1;
        }
        return posL != tableMaskL;
    }

    private void BuildTable(byte[] len, ushort[] table, int maxSymbols, int tableBits)
    {
        if (MakeDecodeTable(maxSymbols, tableBits, len, table))
            throw new InvalidDataException("LZX: failed to build huffman table");
    }

    private bool BuildTableMaybeEmpty(byte[] len, ushort[] table, int maxSymbols, int tableBits)
    {
        if (MakeDecodeTable(maxSymbols, tableBits, len, table))
        {
            for (int i = 0; i < maxSymbols; i++)
                if (len[i] > 0) throw new InvalidDataException("LZX: failed to build (non-empty) huffman table");
            return true;
        }
        return false;
    }

    private int ReadHuffSym(ushort[] table, byte[] len, int maxSymbols, int tableBits)
    {
        EnsureBits(HUFF_MAXBITS);
        int sym = table[PeekBits(tableBits)];
        if (sym >= maxSymbols)
        {
            int i = 1 << (32 - tableBits);
            do
            {
                if ((i >>= 1) == 0) throw new InvalidDataException("LZX: huffman traverse error");
                sym = table[(sym << 1) | ((_bitBuffer & (uint)i) != 0 ? 1 : 0)];
            } while (sym >= maxSymbols);
        }
        RemoveBits(len[sym]);
        return sym;
    }

    private void ReadLengths(byte[] lens, int first, int last)
    {
        for (int x = 0; x < 20; x++) _pretreeLen[x] = (byte)ReadBits(4);
        BuildTable(_pretreeLen, _pretreeTable, PRETREE_MAXSYMBOLS, PRETREE_TABLEBITS);

        for (int x = first; x < last;)
        {
            int z = ReadHuffSym(_pretreeTable, _pretreeLen, PRETREE_MAXSYMBOLS, PRETREE_TABLEBITS);
            if (z == 17)
            {
                int y = (int)ReadBits(4) + 4;
                while (y-- > 0) lens[x++] = 0;
            }
            else if (z == 18)
            {
                int y = (int)ReadBits(5) + 20;
                while (y-- > 0) lens[x++] = 0;
            }
            else if (z == 19)
            {
                int y = (int)ReadBits(1) + 4;
                int z2 = ReadHuffSym(_pretreeTable, _pretreeLen, PRETREE_MAXSYMBOLS, PRETREE_TABLEBITS);
                int delta = lens[x] - z2; if (delta < 0) delta += 17;
                while (y-- > 0) lens[x++] = (byte)delta;
            }
            else
            {
                int delta = lens[x] - z; if (delta < 0) delta += 17;
                lens[x++] = (byte)delta;
            }
        }
    }

    private int Decompress(int outBytes)
    {
        var window = _window;
        uint windowPosn = _windowPosn;
        uint r0 = _r0, r1 = _r1, r2 = _r2;

        long endFrame = (_offset + outBytes) / FRAME_SIZE + 1;

        while (_frame < endFrame)
        {
            if (_resetInterval != 0 && _frame % (uint)_resetInterval == 0)
            {
                if (_blockRemaining != 0) return DecrunchError("bytes remaining at reset interval");
                ResetState();
                r0 = _r0; r1 = _r1; r2 = _r2;
            }

            if (!_headerRead)
            {
                int i = (int)ReadBits(1);
                int intelFilesize;
                if (i != 0) { int hi = (int)ReadBits(16); int lo = (int)ReadBits(16); intelFilesize = (hi << 16) | lo; }
                else intelFilesize = 0;
                _intelFilesize = intelFilesize;
                _headerRead = true;
            }

            uint frameSize = FRAME_SIZE;
            if (_length != 0 && (_length - _offset) < frameSize) frameSize = (uint)(_length - _offset);

            long bytesTodo = _framePosn + frameSize - windowPosn;
            while (bytesTodo > 0)
            {
                if (_blockRemaining == 0)
                {
                    if (_blockType == BLOCKTYPE_UNCOMPRESSED && (_blockLength & 1u) != 0)
                        NextByte();

                    _blockType = (int)ReadBits(3);
                    int bi = (int)ReadBits(16), bj = (int)ReadBits(8);
                    _blockRemaining = _blockLength = (uint)((bi << 8) | bj);

                    switch (_blockType)
                    {
                        case BLOCKTYPE_ALIGNED:
                            for (int i = 0; i < 8; i++) _alignedLen[i] = (byte)ReadBits(3);
                            BuildTable(_alignedLen, _alignedTable, ALIGNED_MAXSYMBOLS, ALIGNED_TABLEBITS);
                            goto case BLOCKTYPE_VERBATIM;
                        case BLOCKTYPE_VERBATIM:
                            ReadLengths(_maintreeLen, 0, 256);
                            ReadLengths(_maintreeLen, 256, NUM_CHARS + (_posnSlots << 3));
                            BuildTable(_maintreeLen, _maintreeTable, MAINTREE_MAXSYMBOLS, MAINTREE_TABLEBITS);
                            if (_maintreeLen[0xE8] != 0) _intelStarted = true;
                            ReadLengths(_lengthLen, 0, NUM_SECONDARY_LENGTHS);
                            _lengthEmpty = BuildTableMaybeEmpty(_lengthLen, _lengthTable, LENGTH_MAXSYMBOLS, LENGTH_TABLEBITS);
                            break;
                        case BLOCKTYPE_UNCOMPRESSED:
                            _intelStarted = true;
                            EnsureBits(16);
                            _bitsLeft = 0; _bitBuffer = 0;
                            Span<byte> buf12 = stackalloc byte[12];
                            for (int i = 0; i < 12; i++) buf12[i] = NextByte();
                            r0 = (uint)(buf12[0] | (buf12[1] << 8) | (buf12[2] << 16) | (buf12[3] << 24));
                            r1 = (uint)(buf12[4] | (buf12[5] << 8) | (buf12[6] << 16) | (buf12[7] << 24));
                            r2 = (uint)(buf12[8] | (buf12[9] << 8) | (buf12[10] << 16) | (buf12[11] << 24));
                            break;
                        default:
                            return DecrunchError("bad block type");
                    }
                }

                int thisRun = (int)Math.Min(_blockRemaining, (uint)Math.Min(bytesTodo, int.MaxValue));
                bytesTodo -= thisRun;
                _blockRemaining -= (uint)thisRun;

                if (_blockType == BLOCKTYPE_UNCOMPRESSED)
                {
                    for (int k = 0; k < thisRun; k++) window[(int)windowPosn + k] = NextByte();
                    windowPosn += (uint)thisRun;
                }
                else
                {
                    bool aligned = _blockType == BLOCKTYPE_ALIGNED;
                    while (thisRun > 0)
                    {
                        int mainElement = ReadHuffSym(_maintreeTable, _maintreeLen, MAINTREE_MAXSYMBOLS, MAINTREE_TABLEBITS);
                        if (mainElement < NUM_CHARS)
                        {
                            window[windowPosn++] = (byte)mainElement;
                            thisRun--;
                            continue;
                        }

                        mainElement -= NUM_CHARS;
                        int matchLength = mainElement & NUM_PRIMARY_LENGTHS;
                        if (matchLength == NUM_PRIMARY_LENGTHS)
                        {
                            if (_lengthEmpty) return DecrunchError("LENGTH symbol needed but tree is empty");
                            matchLength += ReadHuffSym(_lengthTable, _lengthLen, LENGTH_MAXSYMBOLS, LENGTH_TABLEBITS);
                        }
                        matchLength += MIN_MATCH;

                        uint matchOffset;
                        int slot = mainElement >> 3;
                        if (!aligned)
                        {
                            switch (slot)
                            {
                                case 0: matchOffset = r0; break;
                                case 1: matchOffset = r1; r1 = r0; r0 = matchOffset; break;
                                case 2: matchOffset = r2; r2 = r0; r0 = matchOffset; break;
                                case 3: matchOffset = 1; r2 = r1; r1 = r0; r0 = matchOffset; break;
                                default:
                                    int extra = ExtraBits[slot];
                                    uint verbatim = ReadBits(extra);
                                    matchOffset = PositionBase[slot] - 2 + verbatim;
                                    r2 = r1; r1 = r0; r0 = matchOffset;
                                    break;
                            }
                        }
                        else
                        {
                            switch (slot)
                            {
                                case 0: matchOffset = r0; break;
                                case 1: matchOffset = r1; r1 = r0; r0 = matchOffset; break;
                                case 2: matchOffset = r2; r2 = r0; r0 = matchOffset; break;
                                default:
                                    int extra = ExtraBits[slot];
                                    matchOffset = PositionBase[slot] - 2;
                                    if (extra > 3)
                                    {
                                        extra -= 3;
                                        uint verbatim = ReadBits(extra);
                                        matchOffset += verbatim << 3;
                                        matchOffset += (uint)ReadHuffSym(_alignedTable, _alignedLen, ALIGNED_MAXSYMBOLS, ALIGNED_TABLEBITS);
                                    }
                                    else if (extra == 3)
                                    {
                                        matchOffset += (uint)ReadHuffSym(_alignedTable, _alignedLen, ALIGNED_MAXSYMBOLS, ALIGNED_TABLEBITS);
                                    }
                                    else if (extra > 0)
                                    {
                                        matchOffset += ReadBits(extra);
                                    }
                                    else matchOffset = 1;
                                    r2 = r1; r1 = r0; r0 = matchOffset;
                                    break;
                            }
                        }

                        if (windowPosn + (uint)matchLength > _windowSize) return DecrunchError("match ran over window wrap");

                        int rundest = (int)windowPosn;
                        int remain = matchLength;
                        if (matchOffset > windowPosn)
                        {
                            if (matchOffset > _offset) return DecrunchError("match offset beyond LZX stream");
                            int j = (int)(matchOffset - windowPosn);
                            if (j > (int)_windowSize) return DecrunchError("match offset beyond window boundaries");
                            int runsrc = (int)_windowSize - j;
                            if (j < remain)
                            {
                                remain -= j;
                                while (j-- > 0) window[rundest++] = window[runsrc++];
                                runsrc = 0;
                            }
                            while (remain-- > 0) window[rundest++] = window[runsrc++];
                        }
                        else
                        {
                            int runsrc = rundest - (int)matchOffset;
                            while (remain-- > 0) window[rundest++] = window[runsrc++];
                        }

                        thisRun -= matchLength;
                        windowPosn += (uint)matchLength;
                    }
                }
            }

            if (windowPosn - _framePosn != frameSize) return DecrunchError("decode beyond output frame limits");

            if (_bitsLeft > 0) EnsureBits(16);
            if ((_bitsLeft & 15) != 0) RemoveBits(_bitsLeft & 15);

            var frameBytes = new byte[frameSize];
            Array.Copy(window, _framePosn, frameBytes, 0, frameSize);

            if (_intelStarted && _intelFilesize != 0 && _frame <= 32768 && frameSize > 10)
            {
                int curpos = _intelCurpos;
                int filesize = _intelFilesize;
                int dataEnd = (int)frameSize - 10;
                int p = 0;
                while (p < dataEnd)
                {
                    if (frameBytes[p++] != 0xE8) { curpos++; continue; }
                    int absOff = frameBytes[p] | (frameBytes[p + 1] << 8) | (frameBytes[p + 2] << 16) | (frameBytes[p + 3] << 24);
                    if (absOff >= -curpos && absOff < filesize)
                    {
                        int relOff = absOff >= 0 ? absOff - curpos : absOff + filesize;
                        frameBytes[p] = (byte)relOff;
                        frameBytes[p + 1] = (byte)(relOff >> 8);
                        frameBytes[p + 2] = (byte)(relOff >> 16);
                        frameBytes[p + 3] = (byte)(relOff >> 24);
                    }
                    p += 4;
                    curpos += 5;
                }
                _intelCurpos += (int)frameSize;
            }
            else if (_intelFilesize != 0) _intelCurpos += (int)frameSize;

            int writeCount = Math.Min(outBytes - _output.Count, (int)frameSize);
            for (int k = 0; k < writeCount; k++) _output.Add(frameBytes[k]);
            _offset += writeCount;

            _framePosn += frameSize;
            _frame++;
            if (windowPosn == _windowSize) windowPosn = 0;
            if (_framePosn == _windowSize) _framePosn = 0;

            if (_output.Count >= outBytes) break;
        }

        _windowPosn = windowPosn; _r0 = r0; _r1 = r1; _r2 = r2;
        return _output.Count >= outBytes ? 0 : DecrunchError("ran out of frames before producing requested bytes");
    }

    private int DecrunchError(string msg) { Error = "LZX DECRUNCH: " + msg; return 1; }
}
