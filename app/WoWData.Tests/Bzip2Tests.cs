using System.Linq;
using WoWData.Core.Mpq;

namespace WoWData.Tests;

/// <summary>
/// The bzip2 decoder (MPQ sector compression 0x10, used by a few WotLK files) against streams made by Python's standard
/// bz2 module (tests/bzip2 vectors below, made by a script from the plain data the tests rebuild): short text, RLE1 run
/// edges (runs of 3-5, 255-261 and 1000 bytes), 5000 pseudo-random bytes, and two 250,000-byte multi-block streams.
/// A corrupted stream must raise, never return wrong bytes.
/// </summary>
public class Bzip2Tests
{
    private static byte[] Lcg(int n, uint seed = 12345)
    {
        var o = new byte[n];
        uint x = seed;
        for (int i = 0; i < n; i++) { x = (x * 1103515245 + 12345) & 0x7fffffff; o[i] = (byte)(x >> 16); }
        return o;
    }

    private static byte[] Runs()
    {
        int[] lens = { 1, 2, 3, 4, 5, 6, 255, 256, 259, 260, 261, 1000, 4, 4, 4 };
        return lens.SelectMany((l, i) => Enumerable.Repeat((byte)(65 + i % 26), l)).ToArray();
    }

    private static void Check(string b64, byte[] expected)
    {
        var c = Convert.FromBase64String(b64);
        Assert.Equal(expected, Bzip2.Decompress(c, 0, expected.Length));
        // the MPQ form: compression mask 0x10 before the stream
        var withMask = new byte[c.Length + 1];
        withMask[0] = 0x10;
        c.CopyTo(withMask, 1);
        Assert.Equal(expected, Bzip2.Decompress(withMask, 1, expected.Length));
    }

    [Fact] public void Hello() => Check(Hello_, System.Text.Encoding.ASCII.GetBytes("hello, world\n"));
    [Fact] public void RunEdges() => Check(Runs_, Runs());
    [Fact] public void Random5000() => Check(Random5000_, Lcg(5000));
    [Fact] public void MultiBlockRuns() => Check(MultiblockRuns_, Enumerable.Range(0, 250000).Select(i => (byte)((i / 1000) % 256)).ToArray());
    [Fact] public void MultiBlockPeriod() => Check(MultiblockPeriod_, Enumerable.Range(0, 250000).Select(i => (byte)((i % 997) * 7 % 256)).ToArray());

    [Fact]
    public void CorruptStreamRaises()
    {
        var c = Convert.FromBase64String(Random5000_);
        c[c.Length / 2] ^= 0x20;
        Assert.ThrowsAny<MpqException>(() => Bzip2.Decompress(c, 0, 5000));
        var h = Convert.FromBase64String(Hello_);
        h[0] = (byte)'X';
        Assert.ThrowsAny<MpqException>(() => Bzip2.Decompress(h, 0, 13));
    }

    private static readonly string Hello_ =
        "QlpoOTFBWSZTWVSkl4QAAALRgAAQQAQGRJCAIAAxADAgaGIASdSyHz8XckU4UJBUpJeE";

    private static readonly string Runs_ =
        "QlpoOTFBWSZTWeiyCRoAAATEAfAAP/+AgAAIIABURqDQD1BkI01IbSZkyRV9TWdvONOkJkKw7uHjY+pFWQSRAsxECzERFmRB4Owu4gpJOlHxdy" +
        "RThQkOiyCRoA==";

    private static readonly string Random5000_ =
        "QlpoOTFBWSZTWfBq9VQACGD/////////////////////////////////////////////4Ay/fdrvfferq7717b59tPdervJr77e777dtfXb21v" +
        "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";

    private static readonly string MultiblockRuns_ =
        "QlpoMTFBWSZTWX/tyjoAAAH////////////////////////////////////////////oYAZ8AAAAAweHABL//VVQAAAAAAAAGgAAAAAAAADQAG" +
        "gADQAAAAAAAAAAAAABoAAAP/9VVDlNAEwAJgATAAAAAAJgAAAAAAAAAAmBMAAAAAAAAABMAJgDf/6qqgAAEAAAAAAAAAAAAAAAAAAAAAaaAAAA" +
        "AAAAAAAAAAAAAAAAaBFVVP//VVUAAAAAAAAGhoAAAAAAAAaANAANAAAAAAAAAAAAAAAAA0AABlVT/2qqAAb/VUAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAP1RAAAAAAAAAAAAAAAAAAAAAGmgAAAAAAAAAAAAAAAAAAAGj/n7YGbDQZsOBmw8GbEAZsRBmxIGbEwZsUBmxUGbFgZsXBmxg" +
        "GbGQZsaBmxsGbHAZsdBmx4GbHwZsgBmyEGbIgZsjBmyQGbJQZsmBmycGbKAZspBmyoGbKwZssBmy0GbLgZsvBmzAGbMQZsyBmzMGbNAZs1BmzY" +
        "GbNwZs4BmzkGbOgZs7BmzwGbPQZs+Bmz8GbQAZsADNoIM2hAzaGDNgQZtEBm0UGbRgZtHBm0gGbSQZtKBm0sGbTAZtNBm04GbTwZtQBm1EGbUg" +
        "ZtTBm1QGbVQZtWBm1cGbWAZtZBm1oGbWwZtcBm10GbXgZtfBm2AGbYQZtiBm2MGbZAZtlBm2YGbZwZtoBm2kGbagZtrBm2wGbbQZtuBm28GbcA" +
        "ZtxBm3IGbcwZt0Bm3UGbdgZsEDNu4M28AzbyDNvQM29gzb4DNvoM2/AzYMGbfwZuABm4EGbggZuDBm4QGbhQZuGBm4cGbiAZuJBm4oGbiwZsID" +
        "NxgM3GgzccDNx4M3IAzciDNyQM3JgzcoDNyoM3LAzcuDNzAM3Mgzc0DNzYM3OAzc6DNzwM3PgzdADN0IM3RAzdGDN0gM3SgzdMDN04M3UAzdSD" +
        "N1QM3VgzdYDN1oM3XAzdeDN2AM3YgzdkDN2YM3aAzdqDN2wM3bgzdwDN3IM3dAzd2DN3gM3egzd8DN34M3gAzeCDN4QM3hgzeIDN4oM3jAzeOD" +
        "N5AM3kgzeUDN5YM3mAzeaDN5wM3ngzegDN6IM3pAzemDN6gM3qgzesDN64M3sAzeyDN7QM3tgze4DN7oM3vAze+DN8AM3wgzfEDN8YM3yAzfKD" +
        "N8wM3zgzfQDN9IM31AzfXAgQYSDCemBAgwkGEhYECD+gAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABC+5hCgR7/h8fl8/p9ft9/x+f1+/5" +
        "/f9BCGELlmIDlFMQHKKYgOUUxAcopiA5RTEByimIDlFMQHKKYgOUUxAcopiA5RTEByimIDlFMQHKKYgOUUxAcopiBAUUxAgMBgMBgMBRERERER" +
        "ERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERER" +
        "ERERERERERERERERERERERERERERERERE9oiIiIiIiIiIif4/4u5IpwoSD/25R0A";

    private static readonly string MultiblockPeriod_ =
        "QlpoMTFBWSZTWdrny6oAAMj/////////////////////////////////////////////0AX+AAAAAAABJgAJgACYAAAAAAAAAAAAAAAAAAAAAA" +
        "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";
}
