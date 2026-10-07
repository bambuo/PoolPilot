using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PoolPilot.Infrastructure.Brokers;

/// <summary>
/// 币安请求签名（HMAC-SHA256）。
///
/// 两个容易踩的点：
/// <list type="number">
///   <item>**签的是"待签字符串"，不是完整 URL**：把参数按发送顺序拼成
///     <c>k=v&amp;k=v</c>（不含 <c>?</c> 与 <c>signature=</c>），对它取 HMAC。
///     参数顺序、编码方式都必须与实际发出的完全一致，差一个字符签名就废。</item>
///   <item>**时间戳口径是毫秒，且与服务器时间对齐**：本地时钟偏了会被 -1021 拒掉，
///     所以由调用方先取服务器时间、算出偏移量再签。</item>
/// </list>
///
/// 十六进制小写，长度固定 64——币安只认这个形式。
/// </summary>
internal static class BinanceSignature
{
    /// <summary>对查询串签名，返回小写十六进制。</summary>
    public static string Sign(string secret, string query)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        ArgumentNullException.ThrowIfNull(query);

        byte[] hash = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes(query));

        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// 数字参数一律用不变文化格式化。
    /// 用当前文化格式化会把小数点变成逗号（某些区域设置），签名随之失效——
    /// 这类错误只在特定区域设置的机器上出现，极难复现。
    /// </summary>
    public static string Text(decimal value) =>
        value.ToString(CultureInfo.InvariantCulture);
}
