using TdLib;

namespace MeowGram.Core.Proxies;

public enum ProxyKind
{
    Socks5,
    Http,
    MtProto,
}

/// <summary>Параметры прокси. Для MTProto нужен <see cref="Secret"/>, для SOCKS5/HTTP — логин и пароль (необязательно).</summary>
public sealed record ProxySettings(
    ProxyKind Kind,
    string Server,
    int Port,
    string? Username = null,
    string? Password = null,
    string? Secret = null,
    bool HttpOnly = false)
{
    public TdApi.Proxy ToTdProxy() => new()
    {
        Server = Server,
        Port = Port,
        Type = Kind switch
        {
            ProxyKind.Socks5 => new TdApi.ProxyType.ProxyTypeSocks5 { Username = Username ?? string.Empty, Password = Password ?? string.Empty },
            ProxyKind.Http => new TdApi.ProxyType.ProxyTypeHttp { Username = Username ?? string.Empty, Password = Password ?? string.Empty, HttpOnly = HttpOnly },
            ProxyKind.MtProto => new TdApi.ProxyType.ProxyTypeMtproto { Secret = Secret ?? string.Empty },
            _ => throw new ArgumentOutOfRangeException(nameof(Kind), Kind, null),
        },
    };
}

/// <summary>Сохранённый прокси приложения.</summary>
public sealed record SavedProxy(int Id, ProxySettings Settings, bool IsEnabled);
