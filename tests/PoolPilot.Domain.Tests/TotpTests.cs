using System.Text;

using PoolPilot.Domain.Services;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Domain.Tests;

/// <summary>
/// Base32（RFC 4648 §6）编解码。
/// </summary>
[TestClass]
public sealed class Base32Tests
{
    [TestMethod]
    public void Encode_MatchesRfc4648Examples()
    {
        // RFC 4648 §10 的官方例子（这里去掉了填充，本项目一律不输出 '='）
        Assert.AreEqual("", Base32.Encode([]));
        Assert.AreEqual("MY", Base32.Encode("f"u8.ToArray()));
        Assert.AreEqual("MZXQ", Base32.Encode("fo"u8.ToArray()));
        Assert.AreEqual("MZXW6", Base32.Encode("foo"u8.ToArray()));
        Assert.AreEqual("MZXW6YQ", Base32.Encode("foob"u8.ToArray()));
        Assert.AreEqual("MZXW6YTB", Base32.Encode("fooba"u8.ToArray()));
        Assert.AreEqual("MZXW6YTBOI", Base32.Encode("foobar"u8.ToArray()));
    }

    [TestMethod]
    public void Decode_RoundTripsEveryByteValue()
    {
        byte[] original = [.. Enumerable.Range(0, 256).Select(i => (byte)i)];

        CollectionAssert.AreEqual(original, Base32.Decode(Base32.Encode(original)));
    }

    [TestMethod]
    public void Decode_ToleratesHowSecretsActuallyArrive()
    {
        // 从网页/App 复制来的密钥常带空格、连字符、小写与尾随 '='，这些都不该把人挡回去
        byte[] expected = Base32.Decode("MZXW6YTBOI");

        CollectionAssert.AreEqual(expected, Base32.Decode("mzxw6ytboi"));
        CollectionAssert.AreEqual(expected, Base32.Decode("MZXW 6YTB OI"));
        CollectionAssert.AreEqual(expected, Base32.Decode("MZXW-6YTB-OI"));
        CollectionAssert.AreEqual(expected, Base32.Decode("MZXW6YTBOI="));
    }

    [TestMethod]
    public void Decode_RejectsIllegalCharactersAndBadTail()
    {
        Assert.ThrowsExactly<ArgumentException>(() => Base32.Decode("MZXW6YTB0I"));  // 数字 0 不在字母表里
        Assert.ThrowsExactly<ArgumentException>(() => Base32.Decode("MZXW6YTBOI!"));

        // 尾部非零填充位：末字符多出来的那几位必须是 0，否则文本被改过
        Assert.ThrowsExactly<ArgumentException>(() => Base32.Decode("MZXW6YTBOB"));
    }

    /// <summary>TOTP 密钥：20 字节（RFC 4226 §4 的建议长度），且不可能生成出重复的密钥。</summary>
    [TestMethod]
    public void TotpSecret_GeneratesTwentyDistinctBytes()
    {
        TotpSecret first = TotpSecret.Generate();
        TotpSecret second = TotpSecret.Generate();

        Assert.AreEqual(TotpSecret.ByteLength, first.Bytes.Length);
        Assert.AreNotEqual(first.ToBase32(), second.ToBase32());
    }

    [TestMethod]
    public void TotpSecret_ParseRejectsWrongLength()
    {
        // 10 字节（16 个 Base32 字符）是常见的"手动填错长度"，必须拒绝而不是默默算错码
        string tooShort = Base32.Encode(new byte[10]);

        ArgumentException error = Assert.ThrowsExactly<ArgumentException>(() => TotpSecret.Parse(tooShort));
        StringAssert.Contains(error.Message, "20 字节");
    }

    [TestMethod]
    public void TotpSecret_ParseAcceptsBase32WithSpacesAndLowercase()
    {
        string canonical = TotpSecret.Generate().ToBase32();
        string spaced = string.Join(' ', canonical.ToLowerInvariant().Chunk(4).Select(c => new string(c)));

        Assert.AreEqual(canonical, TotpSecret.Parse(spaced).ToBase32());
    }

    /// <summary>密钥不该出现在日志或异常里。</summary>
    [TestMethod]
    public void TotpSecret_DoesNotPrintItself()
    {
        Assert.AreEqual("（已隐藏）", TotpSecret.Generate().ToString());
    }
}

/// <summary>
/// TOTP（RFC 6238）与 HOTP（RFC 4226）。
///
/// 这一类的断言全部对着**标准里公布的向量**，不是"自己跟自己对"：
/// 密钥用 RFC 4226 附录 D / RFC 6238 附录 B 的那串 ASCII "12345678901234567890"
/// （其 Base32 即 <c>GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ</c>）。
/// 一旦实现偏离标准，这些数字就会先红。
/// </summary>
[TestClass]
public sealed class TotpTests
{
    private const string RfcSecretBase32 = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";

    private static TotpSecret Secret() => TotpSecret.Parse(RfcSecretBase32);

    /// <summary>守卫：这串 Base32 解的确实是 RFC 用的那 20 个 ASCII 字节。</summary>
    [TestMethod]
    public void RfcSecret_DecodesToTheAsciiSequenceUsedByTheRfc()
    {
        CollectionAssert.AreEqual(
            Encoding.ASCII.GetBytes("12345678901234567890"),
            TotpSecret.Parse(RfcSecretBase32).Bytes.ToArray());
    }

    /// <summary>RFC 4226 附录 D：计数器 0-9 的六位 HOTP（时间步与计数器是同一个东西）。</summary>
    [TestMethod]
    public void Generate_MatchesRfc4226HotpVectors()
    {
        string[] expected =
        [
            "755224", "287082", "359152", "969429", "338314",
            "254676", "287922", "162583", "399871", "520489",
        ];

        for (int counter = 0; counter < expected.Length; counter++)
        {
            Assert.AreEqual(
                expected[counter],
                TotpGenerator.Generate(Secret(), counter, TotpGenerator.Digits),
                $"计数器 {counter} 的六位码与 RFC 4226 附录 D 不符");
        }
    }

    /// <summary>
    /// RFC 6238 附录 B 的八位向量与对应时刻（SHA-1 行）。
    /// 八位只在这一处用到：标准公布的就是八位，换六位就不成其为"与标准一致"的证据。
    /// </summary>
    [TestMethod]
    public void Generate_MatchesRfc6238TotpVectors()
    {
        (long Seconds, string Expected)[] vectors =
        [
            (59L, "94287082"),
            (1_111_111_109L, "07081804"),
            (1_111_111_111L, "14050471"),
            (1_234_567_890L, "89005924"),
            (2_000_000_000L, "69279037"),
            (20_000_000_000L, "65353130"),
        ];

        foreach ((long seconds, string expected) in vectors)
        {
            DateTimeOffset at = DateTimeOffset.FromUnixTimeSeconds(seconds);
            long step = TotpGenerator.StepAt(at);

            Assert.AreEqual(expected, TotpGenerator.Generate(Secret(), step, 8), $"T={seconds} 的八位码与 RFC 6238 附录 B 不符");

            // 六位是同一个 31 位动态值的后六位（10^6 整除 10^8），所以八位向量的末六位就是该时刻的六位码
            Assert.AreEqual(
                expected[^6..],
                TotpGenerator.Generate(Secret(), step, TotpGenerator.Digits),
                $"T={seconds} 的六位码应为八位向量的末六位");
        }
    }

    /// <summary>窗口 ±1 步都收，±2 步拒——时钟差不该被当成错码，但也只让一步。</summary>
    [TestMethod]
    public void Verify_AcceptsWithinOneStepAndRejectsBeyondIt()
    {
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        TotpSecret secret = Secret();

        foreach (int offset in new[] { -1, 0, 1 })
        {
            string code = TotpGenerator.Generate(secret, TotpGenerator.StepAt(now) + offset, TotpGenerator.Digits);
            Assert.IsTrue(
                TotpGenerator.Verify(secret, code, now, lastAcceptedStep: null).IsValid,
                $"偏移 {offset} 步（{offset * TotpGenerator.StepSeconds} 秒）应当被接受");
        }

        foreach (int offset in new[] { -2, 2, -10, 10 })
        {
            string code = TotpGenerator.Generate(secret, TotpGenerator.StepAt(now) + offset, TotpGenerator.Digits);
            Assert.IsFalse(
                TotpGenerator.Verify(secret, code, now, lastAcceptedStep: null).IsValid,
                $"偏移 {offset} 步不该被接受");
        }
    }

    /// <summary>
    /// **同一个时间步的码可以再用**——这是踩过坑之后的规则（见 <see cref="TotpGenerator.Verify"/>）：
    /// 绑定那一刻用掉的正是当前这一步，紧接着重新登录会拿同一个码进来，
    /// 判成"重放"就会在注册完立刻告诉使用者"验证码不正确"。
    /// </summary>
    [TestMethod]
    public void Verify_AcceptsTheSameStepAgain()
    {
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        TotpSecret secret = Secret();
        string code = TotpGenerator.Generate(secret, TotpGenerator.StepAt(now), TotpGenerator.Digits);

        TotpVerification first = TotpGenerator.Verify(secret, code, now, lastAcceptedStep: null);
        Assert.IsTrue(first.IsValid);

        TotpVerification second = TotpGenerator.Verify(secret, code, now, lastAcceptedStep: first.MatchedStep);
        Assert.IsTrue(second.IsValid, "同一时间步的码不该被当成重放：绑定时用掉的正是这一步");
        Assert.AreEqual(first.MatchedStep, second.MatchedStep);
    }

    /// <summary>
    /// 已经用到某一步之后，**比它更旧的时间步一律不收**——否则上一个窗口的码还能继续用，
    /// 那它就成了一张可以反复使用的凭据。
    /// </summary>
    [TestMethod]
    public void Verify_RejectsStepsOlderThanTheLastAccepted()
    {
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        TotpSecret secret = Secret();

        string laterCode = TotpGenerator.Generate(secret, TotpGenerator.StepAt(now) + 1, TotpGenerator.Digits);
        string earlierCode = TotpGenerator.Generate(secret, TotpGenerator.StepAt(now), TotpGenerator.Digits);
        string muchEarlierCode = TotpGenerator.Generate(secret, TotpGenerator.StepAt(now) - 1, TotpGenerator.Digits);

        TotpVerification later = TotpGenerator.Verify(secret, laterCode, now, lastAcceptedStep: null);
        Assert.IsTrue(later.IsValid);

        Assert.IsFalse(
            TotpGenerator.Verify(secret, earlierCode, now, lastAcceptedStep: later.MatchedStep).IsValid,
            "比上次更旧的时间步应当被拒");

        Assert.IsFalse(
            TotpGenerator.Verify(secret, muchEarlierCode, now, lastAcceptedStep: later.MatchedStep).IsValid,
            "窗口内更旧的时间步同样应当被拒");
    }

    [TestMethod]
    public void Verify_RejectsCodesThatAreNotSixDigits()
    {
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        TotpSecret secret = Secret();
        string code = TotpGenerator.Generate(secret, TotpGenerator.StepAt(now), TotpGenerator.Digits);

        foreach (string malformed in new[] { "", "  ", "12345", "1234567", "12345a", "abcdef", code + "0" })
        {
            Assert.IsFalse(
                TotpGenerator.Verify(secret, malformed, now, lastAcceptedStep: null).IsValid,
                $"「{malformed}」不是六位数字码");
        }
    }

    [TestMethod]
    public void Verify_ToleratesSurroundingWhitespace()
    {
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        TotpSecret secret = Secret();
        string code = TotpGenerator.Generate(secret, TotpGenerator.StepAt(now), TotpGenerator.Digits);

        Assert.IsTrue(TotpGenerator.Verify(secret, $" {code} ", now, lastAcceptedStep: null).IsValid);
    }

    /// <summary>
    /// otpauth:// 不是 RFC，是各家 App 共同遵守的事实约定。这里把它逐段钉住：
    /// 一旦有人改动参数名或忘了转义，扫码就会失败，而失败现场在别人手机上。
    /// </summary>
    [TestMethod]
    public void BuildOtpauthUri_MatchesTheDeFactoFormat()
    {
        TotpSecret secret = Secret();

        string uri = TotpGenerator.BuildOtpauthUri(secret, "PoolPilot", "a+b@example.com");

        Assert.AreEqual(
            "otpauth://totp/PoolPilot%3Aa%2Bb%40example.com"
            + $"?secret={RfcSecretBase32}&issuer=PoolPilot&algorithm=SHA1&digits=6&period=30",
            uri);
    }
}
