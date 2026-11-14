using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using MeowGram.ViewModels.Messenger;

namespace MeowGram.Views.Messenger;

/// <summary>Мелкие преобразования для шаблонов окна чата.</summary>
public static class ComposerConverters
{
    public static readonly IValueConverter IsRecord =
        new FuncValueConverter<ComposerAction, bool>(a => a == ComposerAction.Record);

    public static readonly IValueConverter IsSend =
        new FuncValueConverter<ComposerAction, bool>(a => a == ComposerAction.Send);

    public static readonly IValueConverter IsSave =
        new FuncValueConverter<ComposerAction, bool>(a => a == ComposerAction.Save);

    /// <summary>Описание неподдерживаемого типа сообщения — курсивом.</summary>
    public static readonly IValueConverter ItalicIf =
        new FuncValueConverter<bool, FontStyle>(b => b ? FontStyle.Italic : FontStyle.Normal);

    /// <summary>Загрузка без известного размера — бесконечный индикатор.</summary>
    public static readonly IValueConverter IsZero =
        new FuncValueConverter<double, bool>(v => v <= 0);

    /// <summary>Отступ слева у овала волны (между четвёрками шире).</summary>
    public static readonly IValueConverter LeftMargin =
        new FuncValueConverter<double, Thickness>(v => new Thickness(v, 0, 0, 0));

    /// <summary>Проигранные овалы — яркие, остальные — 40%.</summary>
    public static readonly IValueConverter PlayedOpacity =
        new FuncValueConverter<bool, double>(played => played ? 1 : 0.4);
}
