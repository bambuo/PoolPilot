using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Domain.Tests;

/// <summary>
/// 口令哈希（PBKDF2-HMAC-SHA256）。
///
/// 断言的重点不是"能算出某个具体字符串"（那取决于随机盐），而是三条性质：
/// 用对的能验过、用错的一定验不过、盐每次不同。
/// </summary>
[TestClass]
public sealed class PasswordHashTests
{
    [TestMethod]
    public void Verify_AcceptsTheRightPasswordAndRejectsEverythingElse()
    {
        PasswordHash hash = PasswordHash.FromPassword("correct horse battery");

        Assert.IsTrue(hash.Verify("correct horse battery"));
        Assert.IsFalse(hash.Verify("correct horse batter"));
        Assert.IsFalse(hash.Verify("Correct horse battery"));
        Assert.IsFalse(hash.Verify(""));
        Assert.IsFalse(hash.Verify("correct horse battery "));
    }

    /// <summary>盐每次独立随机：两个人用同一个口令，库里的两行也必须不同（否则一眼看出谁跟谁一样）。</summary>
    [TestMethod]
    public void FromPassword_UsesFreshSaltEveryTime()
    {
        PasswordHash first = PasswordHash.FromPassword("same password twice");
        PasswordHash second = PasswordHash.FromPassword("same password twice");

        Assert.AreNotEqual(first.Encoded, second.Encoded);
        Assert.IsTrue(first.Verify("same password twice"));
        Assert.IsTrue(second.Verify("same password twice"));
    }

    [TestMethod]
    public void Encoded_RoundTripsThroughParse()
    {
        PasswordHash original = PasswordHash.FromPassword("round trip");

        PasswordHash restored = PasswordHash.Parse(original.Encoded);

        Assert.AreEqual(original.Encoded, restored.Encoded);
        Assert.IsTrue(restored.Verify("round trip"));
    }

    /// <summary>格式不对时立刻拒绝，而不是留一个"永远验不过"的账号在那儿让人反复试。</summary>
    [TestMethod]
    public void Parse_RejectsMalformedStorage()
    {
        foreach (string malformed in new[]
                 {
                     "",
                     "   ",
                     "plain-text-password",
                     "pbkdf2-sha256$210000$onlythree",
                     "pbkdf2-sha256$abc$c2FsdA==$aGFzaA==",
                     "pbkdf2-sha256$0$c2FsdA==$aGFzaA==",
                     "sha256$210000$c2FsdA==$aGFzaA==",
                 })
        {
            Assert.ThrowsExactly<ArgumentException>(
                () => PasswordHash.Parse(malformed),
                $"「{malformed}」不应被当作合法的口令哈希");
        }
    }

    /// <summary>篡改过的哈希必须验不过（这条守的是"比较真的在比"）。</summary>
    [TestMethod]
    public void Verify_RejectsTamperedHash()
    {
        PasswordHash hash = PasswordHash.FromPassword("tamper me");
        string[] parts = hash.Encoded.Split('$');

        byte[] key = Convert.FromBase64String(parts[3]);
        key[0] ^= 0xFF;

        PasswordHash tampered = PasswordHash.Parse($"{parts[0]}${parts[1]}${parts[2]}${Convert.ToBase64String(key)}");

        Assert.IsFalse(tampered.Verify("tamper me"));
    }

    [TestMethod]
    public void ValidatePolicy_EnforcesLengthAndNonBlank()
    {
        Assert.ThrowsExactly<ArgumentException>(() => PasswordHash.FromPassword("short"));
        Assert.ThrowsExactly<ArgumentException>(() => PasswordHash.FromPassword("1234567"));
        Assert.ThrowsExactly<ArgumentException>(() => PasswordHash.FromPassword(new string(' ', 12)));
        Assert.ThrowsExactly<ArgumentException>(() => PasswordHash.FromPassword(new string('x', PasswordHash.MaxLength + 1)));

        // 长度刚好达标即可（8 位）
        Assert.IsTrue(PasswordHash.FromPassword("12345678").Verify("12345678"));
    }

    /// <summary>哈希不该被顺手打进日志。</summary>
    [TestMethod]
    public void ToString_DoesNotLeakTheHash()
    {
        string text = PasswordHash.FromPassword("do not log me").ToString();

        Assert.DoesNotContain("$", text.Replace("pbkdf2-sha256$", "", StringComparison.Ordinal));
        StringAssert.Contains(text, "已隐藏");
    }
}

/// <summary>
/// 登录邮箱：归一化与"明显不是邮箱"的拒绝。
/// </summary>
[TestClass]
public sealed class EmailAddressTests
{
    [TestMethod]
    public void Parse_NormalizesCaseAndSurroundingWhitespace()
    {
        EmailAddress email = EmailAddress.Parse("  Someone@Example.COM ");

        Assert.AreEqual("someone@example.com", email.Value);
        Assert.AreEqual(EmailAddress.Parse("someone@example.com"), email);
    }

    [TestMethod]
    public void Parse_AcceptsOrdinaryAddresses()
    {
        foreach (string raw in new[]
                 {
                     "a@b.co",
                     "first.last@sub.example.com",
                     "user+tag@example.com",
                     "x_1-2@example.io",
                 })
        {
            Assert.AreEqual(raw, EmailAddress.Parse(raw).Value, $"「{raw}」应当被接受");
        }
    }

    [TestMethod]
    public void Parse_RejectsThingsThatAreObviouslyNotAddresses()
    {
        foreach (string raw in new[]
                 {
                     "",
                     "   ",
                     "no-at-sign",
                     "two@@example.com",
                     "@example.com",
                     "user@",
                     "user@localhost",
                     "user@.com",
                     "user@example.",
                     "user@exa..mple.com",
                     ".user@example.com",
                     "user.@example.com",
                     "us er@example.com",
                     "user@exa mple.com",
                     "user@@example.com",
                 })
        {
            Assert.ThrowsExactly<ArgumentException>(
                () => EmailAddress.Parse(raw),
                $"「{raw}」不应被当作邮箱");
        }
    }

    [TestMethod]
    public void Parse_RejectsOverlongAddresses()
    {
        string local = new('a', 65);
        Assert.ThrowsExactly<ArgumentException>(() => EmailAddress.Parse($"{local}@example.com"));

        string longEnoughToExceedTotal = new string('a', 250) + "@example.com";
        Assert.ThrowsExactly<ArgumentException>(() => EmailAddress.Parse(longEnoughToExceedTotal));
    }
}
