namespace MeowGram.Core.Messaging;

/// <summary>Место сообщения в серии подряд идущих сообщений одного отправителя.</summary>
public enum SeriesPosition
{
    Single,
    First,
    Middle,
    Last,
}

/// <summary>Сообщение для группировки: только то, что на неё влияет.</summary>
public readonly record struct GroupingItem(long Id, long SenderKey, bool IsOutgoing, bool IsService, DateTimeOffset Date);

/// <summary>Результат для одного сообщения: место в серии и разделители перед ним.</summary>
public readonly record struct GroupingResult(SeriesPosition Position, bool DateSeparatorBefore, bool UnreadSeparatorBefore)
{
    /// <summary>Первое в серии: в группах над ним имя отправителя.</summary>
    public bool StartsSeries => Position is SeriesPosition.Single or SeriesPosition.First;

    /// <summary>Последнее в серии: в группах рядом аватар, после серии отступ.</summary>
    public bool EndsSeries => Position is SeriesPosition.Single or SeriesPosition.Last;
}

/// <summary>Скругления пузыря по часовой стрелке от левого верхнего угла.</summary>
public readonly record struct BubbleCorners(double TopLeft, double TopRight, double BottomRight, double BottomLeft);

/// <summary>Серии сообщений, разделители дат и «непрочитанных», скругления пузырей по макету (18 / 6).</summary>
public static class MessageGrouping
{
    public const double LargeRadius = 18;
    public const double SmallRadius = 6;

    /// <summary>Пауза, после которой сообщения того же отправителя начинают новую серию.</summary>
    public static readonly TimeSpan SeriesGap = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Группирует ленту (по возрастанию времени). <paramref name="unreadAfterId"/> — последнее прочитанное входящее
    /// на момент открытия чата: разделитель «Непрочитанные» встаёт перед первым входящим новее него (0 — без разделителя).
    /// </summary>
    public static GroupingResult[] Compute(IReadOnlyList<GroupingItem> items, long unreadAfterId, TimeZoneInfo zone)
    {
        var results = new GroupingResult[items.Count];
        var unreadPlaced = unreadAfterId <= 0;
        var breaks = new bool[items.Count];

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var dateBefore = i == 0 || LocalDate(items[i - 1].Date, zone) != LocalDate(item.Date, zone);
            var unreadBefore = false;
            if (!unreadPlaced && !item.IsOutgoing && !item.IsService && item.Id > unreadAfterId)
            {
                unreadBefore = true;
                unreadPlaced = true;
            }

            breaks[i] = i == 0 || dateBefore || unreadBefore || !SameSeries(items[i - 1], item);
            results[i] = new GroupingResult(SeriesPosition.Single, dateBefore, unreadBefore);
        }

        for (var i = 0; i < items.Count; i++)
        {
            var starts = breaks[i];
            var ends = i == items.Count - 1 || breaks[i + 1];
            var position = (starts, ends) switch
            {
                (true, true) => SeriesPosition.Single,
                (true, false) => SeriesPosition.First,
                (false, true) => SeriesPosition.Last,
                _ => SeriesPosition.Middle,
            };
            results[i] = results[i] with { Position = position };
        }

        return results;
    }

    /// <summary>Подряд, тот же отправитель и направление, не служебные, без долгой паузы.</summary>
    public static bool SameSeries(GroupingItem previous, GroupingItem next) =>
        !previous.IsService && !next.IsService
        && previous.IsOutgoing == next.IsOutgoing
        && previous.SenderKey == next.SenderKey
        && next.Date - previous.Date < SeriesGap;

    /// <summary>
    /// Скругления по макету: у входящих хвост — малый левый нижний угол, у не первых в серии мал и левый верхний;
    /// у исходящих — зеркально справа.
    /// </summary>
    public static BubbleCorners Corners(SeriesPosition position, bool outgoing)
    {
        var first = position is SeriesPosition.Single or SeriesPosition.First;
        var top = first ? LargeRadius : SmallRadius;
        return outgoing
            ? new BubbleCorners(LargeRadius, top, SmallRadius, LargeRadius)
            : new BubbleCorners(top, LargeRadius, LargeRadius, SmallRadius);
    }

    private static DateOnly LocalDate(DateTimeOffset date, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(date, zone).DateTime);
}
