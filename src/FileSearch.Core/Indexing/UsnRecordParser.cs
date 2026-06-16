using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace FileSearch.Core.Indexing;

internal static class UsnRecordParser
{
    private const int BufferHeaderBytes = 8;
    private const int UsnRecordV2MinimumBytes = 60;
    private const int UsnRecordV3MinimumBytes = 76;

    public static bool TryParseBuffer(
        byte[] buffer,
        int bytesReturned,
        out long nextUsn,
        out List<UsnChangeRecord> records,
        out string error)
    {
        nextUsn = 0;
        records = new List<UsnChangeRecord>();
        error = string.Empty;

        if (bytesReturned < BufferHeaderBytes)
        {
            error = "USN buffer did not include the next cursor.";
            return false;
        }

        nextUsn = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(0, BufferHeaderBytes));
        var offset = BufferHeaderBytes;

        while (offset < bytesReturned)
        {
            if (bytesReturned - offset < 8)
            {
                error = "USN record header is truncated.";
                return false;
            }

            var recordLength = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset, 4));
            if (recordLength <= 0 || offset + recordLength > bytesReturned)
            {
                error = "USN record length is invalid.";
                return false;
            }

            var majorVersion = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset + 4, 2));
            UsnChangeRecord record;
            if (majorVersion == 2)
            {
                if (!TryParseV2(buffer.AsSpan(offset, recordLength), out record, out error))
                    return false;
            }
            else if (majorVersion == 3)
            {
                if (!TryParseV3(buffer.AsSpan(offset, recordLength), out record, out error))
                    return false;
            }
            else
            {
                error = $"Unsupported USN record version: {majorVersion}.";
                return false;
            }

            records.Add(record);
            offset += recordLength;
        }

        return true;
    }

    private static bool TryParseV2(ReadOnlySpan<byte> record, out UsnChangeRecord parsed, out string error)
    {
        parsed = null!;
        error = string.Empty;

        if (record.Length < UsnRecordV2MinimumBytes)
        {
            error = "USN v2 record is truncated.";
            return false;
        }

        var fileNameLength = BinaryPrimitives.ReadUInt16LittleEndian(record[56..58]);
        var fileNameOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[58..60]);
        if (!TryReadName(record, fileNameOffset, fileNameLength, out var name, out error))
            return false;

        var timestamp = BinaryPrimitives.ReadInt64LittleEndian(record[32..40]);
        parsed = new UsnChangeRecord(
            BinaryPrimitives.ReadUInt64LittleEndian(record[8..16]).ToString(System.Globalization.CultureInfo.InvariantCulture),
            BinaryPrimitives.ReadUInt64LittleEndian(record[16..24]).ToString(System.Globalization.CultureInfo.InvariantCulture),
            BinaryPrimitives.ReadInt64LittleEndian(record[24..32]),
            DateTime.FromFileTimeUtc(timestamp),
            BinaryPrimitives.ReadUInt32LittleEndian(record[40..44]),
            (FileAttributes)BinaryPrimitives.ReadUInt32LittleEndian(record[52..56]),
            name);
        return true;
    }

    private static bool TryParseV3(ReadOnlySpan<byte> record, out UsnChangeRecord parsed, out string error)
    {
        parsed = null!;
        error = string.Empty;

        if (record.Length < UsnRecordV3MinimumBytes)
        {
            error = "USN v3 record is truncated.";
            return false;
        }

        var fileNameLength = BinaryPrimitives.ReadUInt16LittleEndian(record[72..74]);
        var fileNameOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[74..76]);
        if (!TryReadName(record, fileNameOffset, fileNameLength, out var name, out error))
            return false;

        var timestamp = BinaryPrimitives.ReadInt64LittleEndian(record[48..56]);
        parsed = new UsnChangeRecord(
            Convert.ToHexString(record[8..24]),
            Convert.ToHexString(record[24..40]),
            BinaryPrimitives.ReadInt64LittleEndian(record[40..48]),
            DateTime.FromFileTimeUtc(timestamp),
            BinaryPrimitives.ReadUInt32LittleEndian(record[56..60]),
            (FileAttributes)BinaryPrimitives.ReadUInt32LittleEndian(record[68..72]),
            name);
        return true;
    }

    private static bool TryReadName(
        ReadOnlySpan<byte> record,
        int fileNameOffset,
        int fileNameLength,
        out string name,
        out string error)
    {
        name = string.Empty;
        error = string.Empty;

        if (fileNameLength < 0 ||
            fileNameOffset < 0 ||
            fileNameOffset + fileNameLength > record.Length ||
            fileNameLength % 2 != 0)
        {
            error = "USN record file name span is invalid.";
            return false;
        }

        name = Encoding.Unicode.GetString(record.Slice(fileNameOffset, fileNameLength));
        return true;
    }
}
