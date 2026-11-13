using System.Web;

namespace MeowGram.Core.Proxies;

/// <summary>
/// Разбирает ссылки на прокси Telegram:
/// <c>tg://proxy?server=..&amp;port=..&amp;secret=..</c>, <c>https://t.me/proxy?...</c> (MTProto),
/// <c>tg://socks?server=..&amp;port=..&amp;user=..&amp;pass=..</c>, <c>https://t.me/socks?...</c> (SOCKS5).
/// </summary>
public static class ProxyLinkParser
{
    public static bool TryParse(string? text, out ProxySettings? settings)
    {
        settings = null;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var link = text.Trim();
        if (!link.Contains("://", StringComparison.Ordinal))
            link = "https://" + link;

        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri))
            return false;

        var kind = DetectKind(uri);
        if (kind is null)
            return false;

        var query = HttpUtility.ParseQueryString(uri.Query);
        var server = query["server"];
        if (string.IsNullOrWhiteSpace(server) || !int.TryParse(query["port"], out var port) || port is < 1 or > 65535)
            return false;

        if (kind == ProxyKind.MtProto)
        {
            var secret = query["secret"];
            if (string.IsNullOrWhiteSpace(secret))
                return false;

            settings = new ProxySettings(ProxyKind.MtProto, server, port, Secret: secret);
        }
        else
        {
            settings = new ProxySettings(ProxyKind.Socks5, server, port, Username: NullIfEmpty(query["user"]), Password: NullIfEmpty(query["pass"]));
        }

        return true;
    }

    private static ProxyKind? DetectKind(Uri uri)
    {
        // tg://proxy?... — имя «хоста» и есть тип; https://t.me/proxy?... — тип в пути.
        var name = uri.Scheme.Equals("tg", StringComparison.OrdinalIgnoreCase)
            ? uri.Host
            : IsTelegramHost(uri.Host) ? uri.AbsolutePath.Trim('/') : null;

        return name?.ToLowerInvariant() switch
        {
            "proxy" => ProxyKind.MtProto,
            "socks" => ProxyKind.Socks5,
            _ => null,
        };
    }

    private static bool IsTelegramHost(string host) =>
        host.Equals("t.me", StringComparison.OrdinalIgnoreCase)
        || host.Equals("telegram.me", StringComparison.OrdinalIgnoreCase)
        || host.Equals("telegram.dog", StringComparison.OrdinalIgnoreCase);

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
