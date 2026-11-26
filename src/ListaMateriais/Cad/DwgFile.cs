using System.Text;

namespace ListaMateriais.Cad;

/// <summary>The DWG versions this reader knows, in the order AutoCAD released them.</summary>
internal enum DwgVersion
{
    R2000 = 1015,
    R2004 = 1018,
    R2007 = 1021,
    R2010 = 1024,
    R2013 = 1027,
    R2018 = 1032,
}

/// <summary>
/// The container of a DWG file: finds, unpacks and hands over the parts the map needs (the classes, the
/// table that says where each object is, and the objects themselves). Written from the public description
/// of the format; nothing of the file is sent anywhere and no other program is involved.
/// </summary>
internal sealed class DwgFile
{
    private DwgFile(DwgVersion version, int maintenance, Encoding encoding)
    {
        Version = version;
        Maintenance = maintenance;
        Encoding = encoding;
    }

    public DwgVersion Version { get; }

    /// <summary>Maintenance release of the version (AutoCAD 2010 changed a detail from release 3 on).</summary>
    public int Maintenance { get; }

    public Encoding Encoding { get; }

    public byte[] Classes { get; private set; } = [];

    /// <summary>The object map: for each handle, where its object starts.</summary>
    public byte[] Handles { get; private set; } = [];

    /// <summary>The bytes the offsets of the object map point into: the whole file for R2000, the objects section after.</summary>
    public byte[] Objects { get; private set; } = [];

    public static string Describe(DwgVersion version) => version switch
    {
        DwgVersion.R2000 => "AutoCAD 2000 a 2002",
        DwgVersion.R2004 => "AutoCAD 2004 a 2006",
        DwgVersion.R2007 => "AutoCAD 2007 a 2009",
        DwgVersion.R2010 => "AutoCAD 2010 a 2012",
        DwgVersion.R2013 => "AutoCAD 2013 a 2017",
        _ => "AutoCAD 2018 ou mais novo",
    };

    public static DwgFile Read(byte[] file)
    {
        if (file.Length < 0x100 || file[0] != 'A' || file[1] != 'C')
            throw new AppException("Este arquivo não parece ser um desenho DWG.");
        var magic = Encoding.ASCII.GetString(file, 0, 6);
        if (!int.TryParse(magic.AsSpan(2), out var number))
            throw new AppException("Este arquivo não parece ser um desenho DWG.");
        if (number < 1015)
            throw new AppException($"Este DWG é de uma versão muito antiga do AutoCAD ({magic}). " +
                                   "Abra-o no AutoCAD e salve como versão 2000 ou mais nova.");
        if (number > 1032)
            throw new AppException($"Este DWG é de uma versão do AutoCAD mais nova que o programa conhece ({magic}). " +
                                   "No AutoCAD, use \"Salvar como\" e escolha a versão 2018.");
        var version = number switch
        {
            1015 => DwgVersion.R2000,
            1018 => DwgVersion.R2004,
            1021 => DwgVersion.R2007,
            1024 => DwgVersion.R2010,
            1027 => DwgVersion.R2013,
            1032 => DwgVersion.R2018,
            _ => throw new AppException($"Versão de DWG não reconhecida ({magic})."),
        };
        if (version == DwgVersion.R2007)
            throw new AppException("Este DWG foi salvo pelo AutoCAD 2007 a 2009, uma versão que o programa ainda não lê. " +
                                   "No AutoCAD, use \"Salvar como\" e escolha a versão 2013 (ou 2000), ou exporte para DXF.");

        var codePage = file[0x13] | file[0x14] << 8;
        var result = new DwgFile(version, file[0x12], CodePage(codePage));
        try
        {
            if (version == DwgVersion.R2000)
                result.ReadR2000(file);
            else
                result.ReadR2004(file);
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException or InvalidDataException or OverflowException)
        {
            throw new AppException("O arquivo DWG está danificado ou tem uma estrutura que o programa não entende.", e);
        }
        return result;
    }

    private static Encoding CodePage(int id)
    {
        // The code page ids of the DWG header: 30 is the Windows Latin-1 page, which covers Portuguese.
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return id switch
            {
                30 or 0 => Encoding.GetEncoding(1252),
                29 => Encoding.GetEncoding(1250),
                31 => Encoding.GetEncoding(1251),
                _ => Encoding.GetEncoding(1252),
            };
        }
        catch (ArgumentException)
        {
            return Encoding.Latin1;
        }
    }

    // ---- R2000: the sections are stored plain, in the places the file header lists.
    private void ReadR2000(byte[] file)
    {
        var count = BitConverter.ToInt32(file, 0x15);
        if (count < 3 || count > 16)
            throw new InvalidDataException("Cabeçalho inválido.");
        var sections = new Dictionary<int, (int Address, int Size)>();
        for (var i = 0; i < count; i++)
        {
            var at = 0x19 + i * 9;
            sections[file[at]] = (BitConverter.ToInt32(file, at + 1), BitConverter.ToInt32(file, at + 5));
        }
        var classes = sections[1];
        var handles = sections[2];
        Classes = file.AsSpan(classes.Address, classes.Size).ToArray();
        Handles = file.AsSpan(handles.Address, handles.Size).ToArray();
        Objects = file;
    }

    // ---- R2004 and later: pages, a page map, and a table of sections, compressed and masked.
    private void ReadR2004(byte[] file)
    {
        var header = new byte[0x6C];
        uint seed = 1;
        for (var i = 0; i < header.Length; i++)
        {
            seed = unchecked(seed * 0x343FD + 0x269EC3);
            header[i] = (byte)(file[0x80 + i] ^ (seed >> 16));
        }
        if (Encoding.ASCII.GetString(header, 0, 11) != "AcFssFcAJMB")
            throw new InvalidDataException("Cabeçalho cifrado inválido.");

        var pageMapAddress = BitConverter.ToInt64(header, 0x54) + 0x100;
        var sectionInfoId = BitConverter.ToInt32(header, 0x5C);

        // The page map: pages numbers and sizes, which give the address of each page (they follow each other from 0x100).
        var map = SystemPage(file, (int)pageMapAddress, 0x41630E3B);
        var pages = new Dictionary<int, (long Address, int Size)>();
        long address = 0x100;
        for (var p = 0; p + 8 <= map.Length;)
        {
            var number = BitConverter.ToInt32(map, p);
            var size = BitConverter.ToInt32(map, p + 4);
            p += 8;
            if (number < 0)
                p += 16;
            pages[number] = (address, size);
            address += size;
        }

        if (!pages.TryGetValue(sectionInfoId, out var infoPage))
            throw new InvalidDataException("Tabela de seções não encontrada.");
        var info = SystemPage(file, (int)infoPage.Address, 0x4163003B);
        var sectionsByName = new Dictionary<string, SectionInfo>(StringComparer.Ordinal);
        var count = BitConverter.ToInt32(info, 0);
        var q = 20;
        for (var i = 0; i < count; i++)
        {
            var total = BitConverter.ToInt64(info, q);
            var pageCount = BitConverter.ToInt32(info, q + 8);
            var maxSize = BitConverter.ToInt32(info, q + 12);
            var compressed = BitConverter.ToInt32(info, q + 20);
            var nameBytes = info.AsSpan(q + 32, 64);
            var end = nameBytes.IndexOf((byte)0);
            var name = Encoding.ASCII.GetString(nameBytes[..(end < 0 ? 64 : end)]);
            q += 96;
            var section = new SectionInfo(total, maxSize, compressed == 2);
            for (var j = 0; j < pageCount; j++)
            {
                section.Pages.Add((BitConverter.ToInt32(info, q), BitConverter.ToInt32(info, q + 4), BitConverter.ToInt64(info, q + 8)));
                q += 16;
            }
            sectionsByName[name] = section;
        }

        byte[] Section(string name) =>
            sectionsByName.TryGetValue(name, out var s)
                ? Assemble(file, s, pages)
                : throw new InvalidDataException($"Seção {name} não encontrada.");

        Classes = Section("AcDb:Classes");
        Handles = Section("AcDb:Handles");
        Objects = Section("AcDb:AcDbObjects");
    }

    private sealed record SectionInfo(long Size, int MaxPageSize, bool Compressed)
    {
        public List<(int Number, int Size, long Offset)> Pages { get; } = [];
    }

    /// <summary>A system page: 20 bytes of header, then the (compressed) data.</summary>
    private static byte[] SystemPage(byte[] file, int address, uint expectedType)
    {
        if (address < 0 || address + 20 > file.Length || BitConverter.ToUInt32(file, address) != expectedType)
            throw new InvalidDataException("Página de sistema inválida.");
        var decompressed = BitConverter.ToInt32(file, address + 4);
        var compressedSize = BitConverter.ToInt32(file, address + 8);
        var compressionType = BitConverter.ToInt32(file, address + 12);
        if (decompressed < 0 || decompressed > 0x1000000 || compressedSize < 0 || address + 20 + compressedSize > file.Length)
            throw new InvalidDataException("Página de sistema inválida.");
        return compressionType == 2
            ? Lz77.Decompress(file, address + 20, compressedSize, decompressed)
            : file.AsSpan(address + 20, Math.Min(compressedSize, decompressed)).ToArray();
    }

    /// <summary>
    /// Puts the pages of one section back together. Every page unpacks into a slot of the section's page size
    /// (usually 0x7400 bytes), and its header says which slot.
    /// </summary>
    private static byte[] Assemble(byte[] file, SectionInfo section, Dictionary<int, (long Address, int Size)> pages)
    {
        // Every data page starts with a 32-byte header, masked with a value that depends on where the page sits.
        var found = new List<(int At, int DataSize, int Start)>();
        long length = 0;
        foreach (var (number, _, _) in section.Pages)
        {
            if (number <= 0 || !pages.TryGetValue(number, out var page))
                continue;
            var at = (int)page.Address;
            if (at < 0 || at + 32 > file.Length)
                throw new InvalidDataException("Página fora do arquivo.");
            var mask = 0x4164536Bu ^ (uint)at;
            var dataSize = (int)(BitConverter.ToUInt32(file, at + 8) ^ mask);
            var start = (int)(BitConverter.ToUInt32(file, at + 16) ^ mask);
            if (dataSize < 0 || at + 32 + dataSize > file.Length || start < 0)
                throw new InvalidDataException("Página de seção inválida.");
            found.Add((at, dataSize, start));
            length = Math.Max(length, (long)start + Math.Max(section.MaxPageSize, section.Compressed ? 0 : dataSize));
        }
        // A page never unpacks to much more than a few dozen times its size.
        if (length > 0x30000000 || length > file.Length * 64L + 0x100000)
            throw new InvalidDataException("Seção grande demais.");
        var output = new byte[length];
        foreach (var (at, dataSize, start) in found)
        {
            if (section.Compressed)
                Lz77.Decompress(file, at + 32, dataSize, Math.Min(section.MaxPageSize, output.Length - start), output, start);
            else
                Buffer.BlockCopy(file, at + 32, output, start, dataSize);
        }
        return output;
    }
}

/// <summary>The compression of the DWG pages: a variant of LZ77 where each opcode says how much to copy and from where.</summary>
internal static class Lz77
{
    public static byte[] Decompress(byte[] source, int offset, int length, int outputLength)
    {
        var output = new byte[outputLength];
        Decompress(source, offset, length, outputLength, output, 0);
        return output;
    }

    public static void Decompress(byte[] src, int offset, int length, int outputLength, byte[] dst, int dstOffset)
    {
        var i = offset;
        var end = offset + length;
        var o = dstOffset;
        var oEnd = dstOffset + outputLength;

        byte Next() => i < end ? src[i++] : throw new InvalidDataException("Dados comprimidos incompletos.");

        int LiteralLength(byte opcode)
        {
            var low = opcode & 0xF;
            if (low == 0)
            {
                byte last;
                while ((last = Next()) == 0)
                    low += 0xFF;
                low += 0xF + last;
            }
            return low + 3;
        }

        int CompressedBytes(byte opcode, int bits)
        {
            var count = opcode & bits;
            if (count == 0)
            {
                byte last;
                while ((last = Next()) == 0)
                    count += 0xFF;
                count += last + bits;
            }
            return count + 2;
        }

        byte TwoByteOffset(int plus, ref int offsetValue)
        {
            var first = Next();
            var second = Next();
            offsetValue |= first >> 2;
            offsetValue |= second << 6;
            offsetValue += plus;
            return first;
        }

        byte Copy(int count)
        {
            if (count > end - i || count > oEnd - o)
                throw new InvalidDataException("Dados comprimidos inválidos.");
            Buffer.BlockCopy(src, i, dst, o, count);
            i += count;
            o += count;
            // The stream normally ends with a 0x11 opcode; a missing one just means there is nothing more.
            return i < end ? src[i++] : (byte)0x11;
        }

        try
        {
            Run();
        }
        catch (InvalidDataException) when (o > dstOffset)
        {

            // A page ends with padding the opcodes cannot describe; what was decoded before it is what the page holds,
            // and the rest of the page stays zero. Damage anywhere else shows up when the objects are read.
        }

        void Run()
        {
            var opcode1 = Next();
            if ((opcode1 & 0xF0) == 0)
                opcode1 = Copy(LiteralLength(opcode1));

            while (i < end && o < oEnd && opcode1 != 0x11)
            {
                int compBytes, compOffset = 0;
                if (opcode1 < 0x10 || opcode1 >= 0x40)
                {
                    compBytes = (opcode1 >> 4) - 1;
                    var opcode2 = Next();
                    compOffset = (((opcode1 >> 2) & 3) | (opcode2 << 2)) + 1;
                }
                else if (opcode1 < 0x20)
                {
                    compBytes = CompressedBytes(opcode1, 7);
                    compOffset = (opcode1 & 8) << 11;
                    opcode1 = TwoByteOffset(0x4000, ref compOffset);
                }
                else
                {
                    compBytes = CompressedBytes(opcode1, 0x1F);
                    opcode1 = TwoByteOffset(1, ref compOffset);
                }

                if (compOffset > o - dstOffset || compBytes > oEnd - o)
                {
                    throw new InvalidDataException("Dados comprimidos inválidos.");
                }
                for (var k = 0; k < compBytes; k++, o++)
                    dst[o] = dst[o - compOffset];

                var literals = opcode1 & 3;
                if (literals == 0)
                {
                    opcode1 = Next();
                    if ((opcode1 & 0xF0) == 0)
                        literals = LiteralLength(opcode1);
                }
                if (literals > 0)
                {
                    if (literals > oEnd - o)
                        break;
                    opcode1 = Copy(literals);
                }
            }
        }
    }
}
