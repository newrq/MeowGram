using Avalonia.Data;
using Avalonia.Markup.Xaml;

namespace MeowGram.Localization;

/// <summary>
/// <c>{l:Tr Key}</c> — строка интерфейса, которая сама обновляется при смене языка.
/// </summary>
public sealed class TrExtension : MarkupExtension
{
    public TrExtension()
    {
    }

    public TrExtension(string key)
    {
        Key = key;
    }

    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new ReflectionBinding($"[{Key}]")
        {
            Source = Loc.Instance,
            Mode = BindingMode.OneWay,
        };
}
