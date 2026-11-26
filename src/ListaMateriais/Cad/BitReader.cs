using System.Text;

namespace ListaMateriais.Cad;

/// <summary>
/// Reads the bit-packed data types of a DWG object (B, BB, BS, BL, BD, DD, H, MC...). Bits are taken most
/// significant first, and nothing is aligned to bytes unless the type says so.
/// </summary>
internal sealed class BitReader
{
    private readonly byte[] _data;

    public BitReader(byte[] data, long startBit, long endBit)
    {
        _data = data;
        Position = startBit;
        EndPosition = Math.Min(endBit, (long)data.Length * 8);
    }

    /// <summary>Absolute position in bits.</summary>
    public long Position { get; set; }

    public long EndPosition { get; }

    public bool AtEnd => Position >= EndPosition;

    private static InvalidDataException Overrun() => new("Os dados do desenho terminam antes do esperado.");

    public int Bit()
    {
        if (Position >= EndPosition)
            throw Overrun();
        var bit = (_data[Position >> 3] >> (7 - (int)(Position & 7))) & 1;
        Position++;
        return bit;
    }

    public uint Bits(int count)
    {
        if (Position + count > EndPosition)
            throw Overrun();
        uint value = 0;
        var left = count;
        while (left > 0)
        {
            var offset = (int)(Position & 7);
            var take = Math.Min(left, 8 - offset);
            var chunk = (_data[Position >> 3] >> (8 - offset - take)) & ((1 << take) - 1);
            value = (value << take) | (uint)chunk;
            Position += take;
            left -= take;
        }
        return value;
    }

    public bool B() => Bit() == 1;

    public int BB() => (int)Bits(2);

    public byte RC() => (byte)Bits(8);

    public int RS() => (int)(RC() | (uint)RC() << 8);

    public int RL() => (int)(RC() | (uint)RC() << 8 | (uint)RC() << 16 | (uint)RC() << 24);

    public double RD()
    {
        Span<byte> bytes = stackalloc byte[8];
        for (var i = 0; i < 8; i++)
            bytes[i] = RC();
        return BitConverter.ToDouble(bytes);
    }

    public int BS() => BB() switch
    {
        0 => RS(),
        1 => RC(),
        2 => 0,
        _ => 256,
    };

    public long BL() => BB() switch
    {
        0 => (uint)RL(),
        1 => RC(),
        _ => 0,
    };

    /// <summary>3-bit length followed by that many bytes (the size of an entity's preview picture).</summary>
    public long BLL()
    {
        var length = (int)Bits(3);
        long value = 0;
        for (var i = 0; i < length; i++)
            value |= (long)RC() << (8 * i);
        return value;
    }

    public double BD() => BB() switch
    {
        0 => RD(),
        1 => 1.0,
        2 => 0.0,
        _ => throw new InvalidDataException("Número inválido no desenho."),
    };

    /// <summary>A double stored as the changes from a default value.</summary>
    public double DD(double defaultValue)
    {
        var code = BB();
        if (code == 0)
            return defaultValue;
        if (code == 3)
            return RD();
        var bits = BitConverter.DoubleToUInt64Bits(defaultValue);
        if (code == 2)
        {
            foreach (var index in new[] { 4, 5, 0, 1, 2, 3 })
                bits = (bits & ~(0xFFUL << (8 * index))) | ((ulong)RC() << (8 * index));
        }
        else
        {
            for (var index = 0; index < 4; index++)
                bits = (bits & ~(0xFFUL << (8 * index))) | ((ulong)RC() << (8 * index));
        }
        return BitConverter.UInt64BitsToDouble(bits);
    }

    public (double X, double Y, double Z) BD3() => (BD(), BD(), BD());

    public (double X, double Y) RD2() => (RD(), RD());

    /// <summary>Thickness: one bit says it is zero, otherwise a BD follows.</summary>
    public double BT() => B() ? 0.0 : BD();

    /// <summary>Extrusion (the direction the drawing plane faces): one bit says it is (0, 0, 1).</summary>
    public (double X, double Y, double Z) BE() => B() ? (0.0, 0.0, 1.0) : BD3();

    /// <summary>Object type of AutoCAD 2010 and later.</summary>
    public int BOT() => BB() switch
    {
        0 => RC(),
        1 => RC() + 0x1F0,
        _ => RS(),
    };

    /// <summary>Modular short: 15 bits per 16-bit word, high bit says another word follows.</summary>
    public int MS()
    {
        var result = 0;
        for (var i = 0; i < 2; i++)
        {
            var word = RS();
            result |= (word & 0x7FFF) << (15 * i);
            if ((word & 0x8000) == 0)
                return result;
        }
        throw new InvalidDataException("Tamanho inválido no desenho.");
    }

    /// <summary>Unsigned modular char: 7 bits per byte, high bit says another byte follows.</summary>
    public ulong UMC()
    {
        ulong result = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            var b = RC();
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return result;
        }
        throw new InvalidDataException("Número inválido no desenho.");
    }

    /// <summary>A handle (the identity of an object). <paramref name="code"/> says how the value is to be used.</summary>
    public ulong Handle(out int code)
    {
        code = (int)Bits(4);
        var count = (int)Bits(4);
        ulong value = 0;
        for (var i = 0; i < count; i++)
            value = (value << 8) | RC();
        return value;
    }

    /// <summary>A handle reference resolved against the handle of the object that holds it.</summary>
    public ulong HandleRef(ulong own)
    {
        var value = Handle(out var code);
        return code switch
        {
            6 => own + 1,
            8 => own - 1,
            0xA => own + value,
            0xC => own - value,
            _ => value,
        };
    }

    /// <summary>Text of an R2000/R2004 file: a length and then one byte per character, in the file's code page.</summary>
    public string TV(Encoding encoding)
    {
        var length = BS();
        if (length < 0 || Position + (long)length * 8 > EndPosition)
            throw Overrun();
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
            bytes[i] = RC();
        return encoding.GetString(bytes).TrimEnd('\0');
    }

    /// <summary>Text of an R2007+ file: a length and then UTF-16 characters.</summary>
    public string TU()
    {
        var length = BS();
        if (length < 0 || Position + (long)length * 16 > EndPosition)
            throw Overrun();
        var chars = new char[length];
        for (var i = 0; i < length; i++)
            chars[i] = (char)RS();
        return new string(chars).TrimEnd('\0');
    }

    public void Skip(long bits)
    {
        if (bits < 0 || Position + bits > EndPosition)
            throw Overrun();
        Position += bits;
    }
}
