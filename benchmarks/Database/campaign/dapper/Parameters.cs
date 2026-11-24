namespace DbPerf.IndependentOrm;

// Generated dispatch sees concrete parameter types at every Dapper call site.
// Values are frozen before the timed loop; dynamic mode creates a fresh parameter object.
public sealed class CommonTypedArgs
{
    public long p1 { get; }
    public long p2 { get; }
    public long p3 { get; }
    public long p4 { get; }
    public long p5 { get; }
    public long p6 { get; }
    public long p7 { get; }
    public long p8 { get; }

    public CommonTypedArgs(ReadOnlySpan<long> values)
    {
        if (values.Length is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(values));
        p1 = values.Length >= 1 ? values[0] : 0;
        p2 = values.Length >= 2 ? values[1] : 0;
        p3 = values.Length >= 3 ? values[2] : 0;
        p4 = values.Length >= 4 ? values[3] : 0;
        p5 = values.Length >= 5 ? values[4] : 0;
        p6 = values.Length >= 6 ? values[5] : 0;
        p7 = values.Length >= 7 ? values[6] : 0;
        p8 = values.Length >= 8 ? values[7] : 0;
    }
}

public sealed class LiteralTypedArgs(long tenant, string pattern, long take)
{
    public long p1 { get; } = tenant;
    public string p2 { get; } = pattern;
    public long p3 { get; } = take;
}

// Only the 32-key relation uses 32 (Int32 ordinal, Int64 tenant, Int64 order) triples.
public sealed class RelatedParameters96
{
    public int p1 { get; }
    public long p2 { get; }
    public long p3 { get; }
    public int p4 { get; }
    public long p5 { get; }
    public long p6 { get; }
    public int p7 { get; }
    public long p8 { get; }
    public long p9 { get; }
    public int p10 { get; }
    public long p11 { get; }
    public long p12 { get; }
    public int p13 { get; }
    public long p14 { get; }
    public long p15 { get; }
    public int p16 { get; }
    public long p17 { get; }
    public long p18 { get; }
    public int p19 { get; }
    public long p20 { get; }
    public long p21 { get; }
    public int p22 { get; }
    public long p23 { get; }
    public long p24 { get; }
    public int p25 { get; }
    public long p26 { get; }
    public long p27 { get; }
    public int p28 { get; }
    public long p29 { get; }
    public long p30 { get; }
    public int p31 { get; }
    public long p32 { get; }
    public long p33 { get; }
    public int p34 { get; }
    public long p35 { get; }
    public long p36 { get; }
    public int p37 { get; }
    public long p38 { get; }
    public long p39 { get; }
    public int p40 { get; }
    public long p41 { get; }
    public long p42 { get; }
    public int p43 { get; }
    public long p44 { get; }
    public long p45 { get; }
    public int p46 { get; }
    public long p47 { get; }
    public long p48 { get; }
    public int p49 { get; }
    public long p50 { get; }
    public long p51 { get; }
    public int p52 { get; }
    public long p53 { get; }
    public long p54 { get; }
    public int p55 { get; }
    public long p56 { get; }
    public long p57 { get; }
    public int p58 { get; }
    public long p59 { get; }
    public long p60 { get; }
    public int p61 { get; }
    public long p62 { get; }
    public long p63 { get; }
    public int p64 { get; }
    public long p65 { get; }
    public long p66 { get; }
    public int p67 { get; }
    public long p68 { get; }
    public long p69 { get; }
    public int p70 { get; }
    public long p71 { get; }
    public long p72 { get; }
    public int p73 { get; }
    public long p74 { get; }
    public long p75 { get; }
    public int p76 { get; }
    public long p77 { get; }
    public long p78 { get; }
    public int p79 { get; }
    public long p80 { get; }
    public long p81 { get; }
    public int p82 { get; }
    public long p83 { get; }
    public long p84 { get; }
    public int p85 { get; }
    public long p86 { get; }
    public long p87 { get; }
    public int p88 { get; }
    public long p89 { get; }
    public long p90 { get; }
    public int p91 { get; }
    public long p92 { get; }
    public long p93 { get; }
    public int p94 { get; }
    public long p95 { get; }
    public long p96 { get; }

    public RelatedParameters96(ReadOnlySpan<long> values)
    {
        if (values.Length != 96) throw new ArgumentException("The frozen related contract requires 96 typed parameters.", nameof(values));
        p1 = checked((int)values[0]);
        p2 = values[1];
        p3 = values[2];
        p4 = checked((int)values[3]);
        p5 = values[4];
        p6 = values[5];
        p7 = checked((int)values[6]);
        p8 = values[7];
        p9 = values[8];
        p10 = checked((int)values[9]);
        p11 = values[10];
        p12 = values[11];
        p13 = checked((int)values[12]);
        p14 = values[13];
        p15 = values[14];
        p16 = checked((int)values[15]);
        p17 = values[16];
        p18 = values[17];
        p19 = checked((int)values[18]);
        p20 = values[19];
        p21 = values[20];
        p22 = checked((int)values[21]);
        p23 = values[22];
        p24 = values[23];
        p25 = checked((int)values[24]);
        p26 = values[25];
        p27 = values[26];
        p28 = checked((int)values[27]);
        p29 = values[28];
        p30 = values[29];
        p31 = checked((int)values[30]);
        p32 = values[31];
        p33 = values[32];
        p34 = checked((int)values[33]);
        p35 = values[34];
        p36 = values[35];
        p37 = checked((int)values[36]);
        p38 = values[37];
        p39 = values[38];
        p40 = checked((int)values[39]);
        p41 = values[40];
        p42 = values[41];
        p43 = checked((int)values[42]);
        p44 = values[43];
        p45 = values[44];
        p46 = checked((int)values[45]);
        p47 = values[46];
        p48 = values[47];
        p49 = checked((int)values[48]);
        p50 = values[49];
        p51 = values[50];
        p52 = checked((int)values[51]);
        p53 = values[52];
        p54 = values[53];
        p55 = checked((int)values[54]);
        p56 = values[55];
        p57 = values[56];
        p58 = checked((int)values[57]);
        p59 = values[58];
        p60 = values[59];
        p61 = checked((int)values[60]);
        p62 = values[61];
        p63 = values[62];
        p64 = checked((int)values[63]);
        p65 = values[64];
        p66 = values[65];
        p67 = checked((int)values[66]);
        p68 = values[67];
        p69 = values[68];
        p70 = checked((int)values[69]);
        p71 = values[70];
        p72 = values[71];
        p73 = checked((int)values[72]);
        p74 = values[73];
        p75 = values[74];
        p76 = checked((int)values[75]);
        p77 = values[76];
        p78 = values[77];
        p79 = checked((int)values[78]);
        p80 = values[79];
        p81 = values[80];
        p82 = checked((int)values[81]);
        p83 = values[82];
        p84 = values[83];
        p85 = checked((int)values[84]);
        p86 = values[85];
        p87 = values[86];
        p88 = checked((int)values[87]);
        p89 = values[88];
        p90 = values[89];
        p91 = checked((int)values[90]);
        p92 = values[91];
        p93 = values[92];
        p94 = checked((int)values[93]);
        p95 = values[94];
        p96 = values[95];
    }
}
