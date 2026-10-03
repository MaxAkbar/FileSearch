using System.Buffers.Binary;
using System.Text;

namespace FileSearch.Core.Extractors;

/// <summary>Bounded LZFu/MELA decoding, as specified by MS-OXRTFCP.</summary>
internal static class OutlookRtfCompression
{
    private const int MaxOutputBytes = 4 * 1024 * 1024;
    private const string DictionarySeed = "{\\rtf1\\ansi\\mac\\deff0\\deftab720{\\fonttbl;}{\\f0\\fnil \\froman \\fswiss \\fmodern \\fscript \\fdecor MS Sans SerifSymbolArialTimes New RomanCourier{\\colortbl\\red0\\green0\\blue0\r\n\\par \\pard\\plain\\f0\\fs20\\b\\i\\u\\tab\\tx";

    public static byte[] Decompress(ReadOnlySpan<byte> data)
    {
        if (data.Length < 16) throw new InvalidDataException("The compressed RTF header is incomplete.");
        var compressedSize = BinaryPrimitives.ReadUInt32LittleEndian(data);
        var rawSize = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
        if (rawSize > MaxOutputBytes || compressedSize < 12 || compressedSize > data.Length - 4)
            throw new InvalidDataException("The compressed RTF sizes are invalid or exceed the read limit.");
        var payload = data.Slice(16, checked((int)compressedSize - 12));
        if (magic == 0x414C454D) // MELA: uncompressed payload
        {
            if (payload.Length < rawSize) throw new InvalidDataException("The uncompressed RTF payload is incomplete.");
            return payload[..checked((int)rawSize)].ToArray();
        }
        if (magic != 0x75465A4C) throw new InvalidDataException("The compressed RTF signature is unsupported.");
        if (ComputeCrc(payload) != BinaryPrimitives.ReadUInt32LittleEndian(data[12..]))
            throw new InvalidDataException("The compressed RTF checksum is invalid.");
        var dictionary = new byte[4096];
        var write = Encoding.ASCII.GetBytes(DictionarySeed, dictionary);
        var output = new byte[checked((int)rawSize)];
        var position = 0;
        var read = 0;
        while (read < payload.Length)
        {
            var flags = payload[read++];
            for (var bit = 0; bit < 8; bit++)
            {
                if (read >= payload.Length) throw new InvalidDataException("The compressed RTF payload is incomplete.");
                if ((flags & (1 << bit)) == 0)
                {
                    if (position >= output.Length) throw new InvalidDataException("The compressed RTF exceeds its declared size.");
                    var value = payload[read++];
                    output[position++] = value;
                    dictionary[write] = value;
                    write = (write + 1) & 4095;
                }
                else
                {
                    if (read + 1 >= payload.Length) throw new InvalidDataException("The compressed RTF back-reference is incomplete.");
                    var reference = (payload[read++] << 8) | payload[read++];
                    var offset = reference >> 4;
                    var length = (reference & 15) + 2;
                    if (offset == write)
                    {
                        if (position != output.Length) throw new InvalidDataException("The compressed RTF ended before its declared size.");
                        return output;
                    }
                    if (length > output.Length - position) throw new InvalidDataException("The compressed RTF exceeds its declared size.");
                    for (var i = 0; i < length; i++)
                    {
                        var value = dictionary[offset];
                        offset = (offset + 1) & 4095;
                        output[position++] = value;
                        dictionary[write] = value;
                        write = (write + 1) & 4095;
                    }
                }
            }
        }
        throw new InvalidDataException("The compressed RTF terminator is missing.");
    }

    // MS-OXRTFCP starts the IEEE CRC at zero and does not complement it.
    internal static uint ComputeCrc(ReadOnlySpan<byte> data)
    {
        uint crc = 0;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320U : 0);
        }
        return crc;
    }
}
