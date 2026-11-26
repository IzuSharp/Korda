using System.Text;

namespace DbPerf.Campaign;

internal struct Digest
{
    internal ulong Value;
    internal static Digest Create()=>new(){Value=14695981039346656037UL};
    private void Byte(byte value){Value^=value;Value*=1099511628211UL;}
    internal void Integer(long value){var bits=unchecked((ulong)value);for(var i=0;i<8;i++){Byte((byte)bits);bits>>=8;}}
    internal void Boolean(bool value)=>Byte(value?(byte)1:(byte)0);
    internal void Money(decimal value)=>Integer(checked((long)(value*100m)));
    internal void NullableInteger(long? value){Boolean(value.HasValue);if(value.HasValue)Integer(value.Value);}
    internal void NullableMoney(decimal? value){Boolean(value.HasValue);if(value.HasValue)Money(value.Value);}
    internal void Text(string? value)
    {
        if(value is null){Integer(-1);return;}
        var count=Encoding.UTF8.GetByteCount(value);Integer(count);
        if(count>4096)throw new InvalidOperationException("Field exceeds the frozen workload bound");
        Span<byte> bytes=stackalloc byte[4096];
        var length=Encoding.UTF8.GetBytes(value.AsSpan(),bytes);
        for(var i=0;i<length;i++)Byte(bytes[i]);
    }
}
