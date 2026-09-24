using System;
using System.Globalization;

namespace Lugworm.Core.Duet
{
    /// <summary>Standard CRC-32 (IEEE 802.3, reflected polynomial 0xEDB88320) - the
    /// checksum RRF verifies on rr_upload. Clean-room, textbook table implementation
    /// (the DSF reference client is GPL and must not be copied).</summary>
    public static class Crc32
    {
        static readonly uint[] Table = BuildTable();

        static uint[] BuildTable()
        {
            var t = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                t[i] = c;
            }
            return t;
        }

        public static uint Compute(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            uint crc = 0xFFFFFFFFu;
            for (int i = 0; i < data.Length; i++)
                crc = Table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFFu;
        }

        /// <summary>Lower-case hex without prefix - the format rr_upload expects.</summary>
        public static string HexLower(uint crc) => crc.ToString("x8", CultureInfo.InvariantCulture);

        public sealed class Incremental
        {
            uint _crc = 0xFFFFFFFFu;
            public long Length { get; private set; }
            public void Append(byte[] data, int offset, int count)
            {
                if (data == null) throw new ArgumentNullException(nameof(data));
                for (int i = 0; i < count; i++) _crc = Table[(_crc ^ data[offset + i]) & 0xFF] ^ (_crc >> 8);
                Length += count;
            }
            public uint Value => _crc ^ 0xFFFFFFFFu;
            public string Hex => HexLower(Value);
        }
    }
}
