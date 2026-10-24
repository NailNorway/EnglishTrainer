namespace EnglishTrainer.Application.Dtos;

// ─────────────────────────── Достижения ───────────────────────────

/// <summary>Достижение вместе с прогрессом по нему.</summary>
/// <param name="Target">Порог: сколько нужно, чтобы достижение закрылось.</param>
/// <param name="Progress">
/// Сколько набрано. Обрезается по порогу: «120 из 100» выглядит как ошибка,
/// а не как успех.
/// </param>
/// <param name="UnlockedAt">Когда получено. <c>null</c> — ещё не получено.</param>
public sealed record AchievementDto(
    string Code,
    string Title,
    string Description,
    string Icon,
    int Target,
    int Progress,
    bool Unlocked,
    DateTime? UnlockedAt);

/// <summary>Все достижения пользователя и ближайшая цель.</summary>
/// <param name="NextGoal">
/// Незакрытое достижение, до которого осталось меньше всего. Именно оно
/// превращает список наград в план: «осталось два слова» тянет вернуться
/// сильнее, чем «когда-нибудь будет тысяча ответов».
/// </param>
public sealed record AchievementsDto(
    IReadOnlyList<AchievementDto> Items,
    int UnlockedCount,
    int Total,
    AchievementDto? NextGoal);
