using MegaCrit.Sts2.Core.Models;

namespace PwA.Game;

/// <summary>
/// 稳定手牌引用：c1..cN 绑定在 CardModel 实例上，同一回合内不随出牌重排；
/// 换回合（RoundNumber 变了）清空重来。选牌界面的候选牌也会分到 ref。
/// </summary>
internal static class CardRefs
{
    private static readonly Dictionary<CardModel, string> ByCard = new(ReferenceEqualityComparer.Instance);
    private static readonly Dictionary<string, CardModel> ByRef = new(StringComparer.OrdinalIgnoreCase);
    private static int _next;
    private static int _turn = int.MinValue;

    public static void SyncTurn(int turn)
    {
        if (turn == _turn)
        {
            return;
        }

        _turn = turn;
        ByCard.Clear();
        ByRef.Clear();
        _next = 0;
    }

    public static string RefOf(CardModel card)
    {
        if (ByCard.TryGetValue(card, out var reference))
        {
            return reference;
        }

        _next++;
        reference = $"c{_next}";
        ByCard[card] = reference;
        ByRef[reference] = card;
        return reference;
    }

    public static CardModel? Resolve(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        return ByRef.TryGetValue(reference.Trim(), out var card) && card != null ? card : null;
    }

    public static void Forget(CardModel card)
    {
        if (ByCard.Remove(card, out var reference))
        {
            ByRef.Remove(reference);
        }
    }
}
