using System.Buffers.Binary;
using System.IO;
using System.Text;
using FileSearch.Core.Indexing;

namespace FileSearch.Core.Tests;

public sealed class UsnRecordParserTests
{
    [Fact]
    public void TryParseBufferParsesUsnV2Records()
    {
        var record = BuildV2Record(
            fileReferenceNumber: 10,
            parentFileReferenceNumber: 2,
            usn: 40,
            reason: 0x00000001,
            attributes: (uint)FileAttributes.Archive,
            name: "alpha.txt");
        var buffer = BuildBuffer(nextUsn: 41, record);

        var ok = UsnRecordParser.TryParseBuffer(buffer, buffer.Length, out var nextUsn, out var records, out var error);

        Assert.True(ok, error);
        Assert.Equal(41, nextUsn);
        var parsed = Assert.Single(records);
        Assert.Equal("10", parsed.FileReferenceNumber);
        Assert.Equal("2", parsed.ParentFileReferenceNumber);
        Assert.Equal(40, parsed.Usn);
        Assert.Equal("alpha.txt", parsed.Name);
        Assert.Equal(FileAttributes.Archive, parsed.FileAttributes);
    }

    [Fact]
    public void TryParseBufferParsesUsnV3Records()
    {
        var fileId = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        var parentId = Enumerable.Range(16, 16).Select(i => (byte)i).ToArray();
        var record = BuildV3Record(
            fileId,
            parentId,
            usn: 80,
            reason: 0x00000002,
            attributes: (uint)FileAttributes.Archive,
            name: "beta.txt");
        var buffer = BuildBuffer(nextUsn: 81, record);

        var ok = UsnRecordParser.TryParseBuffer(buffer, buffer.Length, out var nextUsn, out var records, out var error);

        Assert.True(ok, error);
        Assert.Equal(81, nextUsn);
        var parsed = Assert.Single(records);
        Assert.Equal(Convert.ToHexString(fileId), parsed.FileReferenceNumber);
        Assert.Equal(Convert.ToHexString(parentId), parsed.ParentFileReferenceNumber);
        Assert.Equal(80, parsed.Usn);
        Assert.Equal("beta.txt", parsed.Name);
    }

    [Fact]
    public void TryParseBufferRejectsMalformedRecordLength()
    {
        var buffer = new byte[16];
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(0, 8), 10);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(8, 4), 100);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12, 2), 2);

        var ok = UsnRecordParser.TryParseBuffer(buffer, buffer.Length, out _, out _, out var error);

        Assert.False(ok);
        Assert.Contains("length", error, StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] BuildBuffer(long nextUsn, byte[] record)
    {
        var buffer = new byte[8 + record.Length];
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(0, 8), nextUsn);
        record.CopyTo(buffer.AsSpan(8));
        return buffer;
    }

    private static byte[] BuildV2Record(
        ulong fileReferenceNumber,
        ulong parentFileReferenceNumber,
        long usn,
        uint reason,
        uint attributes,
        string name)
    {
        var nameBytes = Encoding.Unicode.GetBytes(name);
        var record = new byte[60 + nameBytes.Length];
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(0, 4), record.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4, 2), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(6, 2), 0);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(8, 8), fileReferenceNumber);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(16, 8), parentFileReferenceNumber);
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(24, 8), usn);
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(32, 8), DateTime.UtcNow.ToFileTimeUtc());
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(40, 4), reason);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(52, 4), attributes);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(56, 2), checked((ushort)nameBytes.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(58, 2), 60);
        nameBytes.CopyTo(record.AsSpan(60));
        return record;
    }

    private static byte[] BuildV3Record(
        byte[] fileReferenceNumber,
        byte[] parentFileReferenceNumber,
        long usn,
        uint reason,
        uint attributes,
        string name)
    {
        var nameBytes = Encoding.Unicode.GetBytes(name);
        var record = new byte[76 + nameBytes.Length];
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(0, 4), record.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4, 2), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(6, 2), 0);
        fileReferenceNumber.CopyTo(record.AsSpan(8, 16));
        parentFileReferenceNumber.CopyTo(record.AsSpan(24, 16));
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(40, 8), usn);
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(48, 8), DateTime.UtcNow.ToFileTimeUtc());
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(56, 4), reason);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(68, 4), attributes);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(72, 2), checked((ushort)nameBytes.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(74, 2), 76);
        nameBytes.CopyTo(record.AsSpan(76));
        return record;
    }
}
