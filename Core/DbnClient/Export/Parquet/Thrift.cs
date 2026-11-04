using System.Text;

namespace DbnClient.Export.Parquet;

/// <summary>
/// Thrift's compact protocol, the little of it Parquet's footer and page
/// headers need: structs, lists, and integer, boolean and binary fields.
/// </summary>
internal sealed class Thrift
{
    private const byte True = 1;
    private const byte False = 2;
    private const byte I32 = 5;
    private const byte I64 = 6;
    private const byte Binary = 8;
    private const byte List = 9;
    private const byte Struct = 12;

    private readonly MemoryStream bytes = new();
    private readonly Stack<short> lastField = new();
    private short last;

    public byte[] ToArray() => bytes.ToArray();

    public Thrift BeginStruct()
    {
        lastField.Push(last);
        last = 0;

        return this;
    }

    public Thrift EndStruct()
    {
        bytes.WriteByte(0);
        last = lastField.Pop();

        return this;
    }

    public Thrift Int(short field, int value)
    {
        Header(field, I32);
        Varint(ZigZag(value));

        return this;
    }

    public Thrift Long(short field, long value)
    {
        Header(field, I64);
        Varint(ZigZag(value));

        return this;
    }

    public Thrift Bool(short field, bool value)
    {
        Header(field, value ? True : False);

        return this;
    }

    public Thrift String(short field, string value)
    {
        Header(field, Binary);
        Bytes(Encoding.UTF8.GetBytes(value));

        return this;
    }

    /// <summary>A struct field: its fields come next, then <see cref="EndStruct"/>.</summary>
    public Thrift StructField(short field)
    {
        Header(field, Struct);

        return BeginStruct();
    }

    /// <summary>A list of structs: write each with <see cref="BeginStruct"/> and <see cref="EndStruct"/>.</summary>
    public Thrift StructList(short field, int count) => ListHeader(field, Struct, count);

    public Thrift IntList(short field, IReadOnlyList<int> values)
    {
        ListHeader(field, I32, values.Count);

        foreach (var v in values)
            Varint(ZigZag(v));

        return this;
    }

    public Thrift StringList(short field, IReadOnlyList<string> values)
    {
        ListHeader(field, Binary, values.Count);

        foreach (var v in values)
            Bytes(Encoding.UTF8.GetBytes(v));

        return this;
    }

    private Thrift ListHeader(short field, byte elementType, int count)
    {
        Header(field, List);

        if (count < 15)
        {
            bytes.WriteByte((byte)(count << 4 | elementType));
        }
        else
        {
            bytes.WriteByte((byte)(0xF0 | elementType));
            Varint((ulong)count);
        }

        return this;
    }

    private void Header(short field, byte type)
    {
        var delta = field - last;

        if (delta is > 0 and <= 15)
        {
            bytes.WriteByte((byte)(delta << 4 | type));
        }
        else
        {
            bytes.WriteByte(type);
            Varint(ZigZag(field));
        }

        last = field;
    }

    private void Bytes(byte[] value)
    {
        Varint((ulong)value.Length);
        bytes.Write(value);
    }

    private void Varint(ulong value)
    {
        while (value >= 0x80)
        {
            bytes.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }

        bytes.WriteByte((byte)value);
    }

    private static ulong ZigZag(long value) => (ulong)(value << 1 ^ value >> 63);
}
