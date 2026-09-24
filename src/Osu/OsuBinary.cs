using System.Text;

namespace OsuCollectionManager.Osu;

/// <summary>BinaryReader with osu!'s string encoding (0x00 = null, 0x0b + ULEB128 length + UTF-8).</summary>
public sealed class OsuReader(Stream stream) : BinaryReader(stream, Encoding.UTF8)
{
    public string? ReadOsuString()
    {
        byte flag = ReadByte();
        if (flag == 0x00) return null;
        if (flag != 0x0b) throw new InvalidDataException($"Invalid string flag 0x{flag:x2} at {BaseStream.Position - 1}");
        int length = (int)ReadUleb128();
        return Encoding.UTF8.GetString(ReadBytes(length));
    }

    public ulong ReadUleb128()
    {
        ulong result = 0;
        int shift = 0;
        while (true)
        {
            byte b = ReadByte();
            result |= (ulong)(b & 0x7f) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
        }
    }

    public void Skip(int bytes) => BaseStream.Seek(bytes, SeekOrigin.Current);
}

public sealed class OsuWriter(Stream stream) : BinaryWriter(stream, Encoding.UTF8)
{
    public void WriteOsuString(string? value)
    {
        if (value is null)
        {
            Write((byte)0x00);
            return;
        }
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        Write((byte)0x0b);
        ulong length = (ulong)bytes.Length;
        do
        {
            byte b = (byte)(length & 0x7f);
            length >>= 7;
            if (length != 0) b |= 0x80;
            Write(b);
        } while (length != 0);
        Write(bytes);
    }
}
