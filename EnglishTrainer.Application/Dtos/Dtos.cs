using EnglishTrainer.Domain.Enums;

namespace EnglishTrainer.Application.Dtos;

// ─────────────────────────── Аутентификация ───────────────────────────

/// <param name="Username">
/// Желаемый ник. Если не указан, он подбирается из email; если указан и занят —
/// регистрация отклоняется, потому что человек уже ввёл конкретный ник.
/// </param>
public sealed record RegisterRequestDto(
    string Email,
    string Password,
    string? DisplayName,
    string? TimeZoneId,
    string? Username = null);

/// <summary>Вход в приложение.</summary>
/// <param name="Login">
/// Email или ник. Человек помнит ник, который сам придумал, а email под рукой
/// не всегда, поэтому принимается и то и другое: оба хранятся в нижнем регистре,
/// и приведение у них одинаковое.
/// </param>
public sealed record LoginRequestDto(string Login, string Password);

/// <summary>Запрос ссылки для сброса пароля.</summary>
/// <param name="Login">Email или ник — то, что человек помнит.</param>
public sealed record ForgotPasswordRequestDto(string Login);

/// <summary>Установка нового пароля по ссылке из письма.</summary>
public sealed record ResetPasswordRequestDto(string Token, string NewPassword);

/// <summary>
/// Куда уходит письмо со ссылкой для сброса пароля.
/// </summary>
/// <param name="Delivery">
/// <c>email</c> — письмо отправляется почтой; <c>log</c> — выводится в журнал
/// сервера, потому что почтовый провайдер не настроен.
/// </param>
public sealed record PasswordResetMethodDto(string Delivery);

/// <summary>Смена пароля изнутри приложения.</summary>
public sealed record ChangePasswordRequestDto(string CurrentPassword, string NewPassword);

public sealed record AuthResponseDto(string AccessToken, DateTime ExpiresAtUtc, UserDto User);

// ─────────────────────────── Пользователь ───────────────────────────

public sealed record UserDto(
    Guid PublicId,
    string Email,
    string Username,
    string DisplayName,
    /// <summary>Адрес аватара или <c>null</c>, если фото не загружено: тогда рисуются инициалы.</summary>
    string? AvatarUrl,
    string TimeZoneId,
    DateTime CreatedAt,
    bool TelegramLinked);

public sealed record UserSettingsDto(
    int NewWordsPerDay,
    int MaxCardsPerSession,
    int DailyGoal,
    bool DailyReminderEnabled,
    TimeOnly ReminderTime,
    bool ShowTranscriptionUpfront);

public sealed record UpdateSettingsRequestDto(
    int? NewWordsPerDay,
    int? MaxCardsPerSession,
    int? DailyGoal,
    bool? DailyReminderEnabled,
    TimeOnly? ReminderTime,
    bool? ShowTranscriptionUpfront);

// ─────────────────────────── Слова и колоды ───────────────────────────

public sealed record WordExampleDto(
    string TextEn,
    string? TextRu,
    string? AudioUrl,
    string? Attribution);

public sealed record WordTranslationDto(string Text, string? Sense, bool IsPrimary);

public sealed record WordDto(
    int Id,
    string Lemma,
    string PartOfSpeech,
    string? IpaUk,
    string? IpaUs,
    string? AudioUrl,
    CefrLevel Level,
    IReadOnlyList<WordTranslationDto> Translations);

public sealed record WordDetailDto(
    int Id,
    string Lemma,
    string PartOfSpeech,
    string? IpaUk,
    string? IpaUs,
    int? FrequencyRank,
    CefrLevel Level,
    IReadOnlyList<WordTranslationDto> Translations,
    IReadOnlyList<WordExampleDto> Examples);

/// <summary>
/// Слово вместе с состоянием повторения — строка страниц «В работе»,
/// «Пора повторить» и «Выучено».
/// </summary>
/// <param name="DueAtUtc">Когда слово снова нужно повторить.</param>
/// <param name="IntervalDays">
/// Текущий интервал: по нему видно, насколько слово закрепилось. Для выученных
/// он не меньше <see cref="EnglishTrainer.Domain.Entities.CardState.LearnedIntervalDays"/>.
/// </param>
public sealed record WordProgressDto(
    WordDto Word,
    CardPhase Phase,
    bool IsLearned,
    double IntervalDays,
    DateTime DueAtUtc,
    DateTime? LastReviewedAt);

/// <summary>Колода вместе с прогрессом конкретного пользователя.</summary>
/// <param name="IsOwned">
/// Личная колода этого пользователя: её можно дополнять и удалять. Системные
/// и чужие колоды в списке тоже видны (первые — всегда), но правки запрещены.
/// </param>
public sealed record DeckDto(
    int Id,
    string Slug,
    string Title,
    string? Description,
    int TotalWords,
    int StartedWords,
    int LearnedWords,
    bool IsOwned = false);

// ─────────────────────────── Обучение ───────────────────────────

/// <summary>
/// Карточка для показа.
/// </summary>
/// <remarks>
/// Правильного ответа здесь <b>нет</b>, и это главное свойство контракта.
/// Передаётся только <see cref="Prompt"/> — то, что пользователь должен прочитать, —
/// а проверка ответа остаётся на сервере. Если бы карточка содержала ответ,
/// его было бы видно на экране, и упражнение теряло бы смысл.
/// </remarks>
public sealed record StudyCardDto(
    int ItemId,
    int Position,
    int WordId,
    ExerciseKind Exercise,
    SessionItemKind Kind,
    /// <summary>Текст вопроса: английское слово для выбора варианта, перевод — для ввода.</summary>
    string Prompt,
    /// <summary>Язык вопроса (<c>en</c> или <c>ru</c>): клиенту нужно знать, что показывать.</summary>
    string PromptLanguage,
    string PartOfSpeech,
    /// <summary>Транскрипция как подсказка. Показывается, когда вопрос на английском.</summary>
    string? IpaUk,
    string? IpaUs,
    string? AudioUrl,
    /// <summary>Варианты ответа для упражнения с выбором. Пусто для ввода.</summary>
    IReadOnlyList<string> Options,
    /// <summary>Подсказка: сколько букв в слове — для упражнений на написание.</summary>
    int? AnswerLength,
    /// <summary>
    /// Текст, который клиент должен произнести вслух, а не показать.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Появляется только у упражнения на слух, и это единственное место, где
    /// правильный ответ всё-таки уходит в карточку. По-другому не выходит:
    /// озвучивает браузер, а значит, текст должен быть на клиенте — файлов
    /// с записями в словаре нет.
    /// </para>
    /// <para>
    /// Правило «карточка не содержит ответа» при этом не нарушено по существу:
    /// оно защищало от того, что ответ <b>виден на экране</b> до попытки.
    /// Здесь его на экране нет — клиент обязан произнести текст и не рисовать
    /// его. Упражнения на ввод и на написание, как и раньше, не содержат ответа
    /// ни в одном поле, и это закреплено тестом.
    /// </para>
    /// </remarks>
    string? SpokenText = null);

public sealed record StudySessionDto(
    int SessionId,
    StudySessionStatus Status,
    StudyCardDto? CurrentCard,
    int Position,
    int TotalItems,
    int Remaining,
    int DueCount,
    int NewCount,
    DateOnly LocalDate,
    /// <summary>
    /// Когда наступит ближайшее повторение — заполняется только тогда, когда
    /// сейчас повторять нечего. Для непустой очереди остаётся <c>null</c>.
    /// </summary>
    /// <remarks>
    /// Без этой даты ответ «карточек нет» бесполезен: человек не понимает,
    /// ждать ему минуту или сутки, и решает, что приложение сломалось.
    /// </remarks>
    DateTime? NextDueAtUtc = null);

/// <summary>Отправка ответа на карточку.</summary>
/// <param name="ItemId">Идентификатор карточки, которую проверяем.</param>
/// <param name="Answer">Введённый текст. Для выбора — сам вариант ответа.</param>
/// <param name="Rating">Явная оценка. Если не задана, выводится из правильности ответа.</param>
/// <param name="ElapsedMs">Время ответа: слишком быстрое — признак угадывания.</param>
public sealed record SubmitAnswerRequestDto(int ItemId, string? Answer, ReviewRating? Rating, int? ElapsedMs);

/// <summary>Результат проверки ответа.</summary>
/// <param name="MatchedWithTypo">
/// Ответ зачтён с опечаткой: написание отличается от эталона, но не настолько,
/// чтобы считать ответ неверным. Интерфейс показывает эталон — оценку это не меняет.
/// </param>
public sealed record AnswerResultDto(
    bool IsCorrect,
    string CorrectAnswer,
    string? IpaUk,
    string? IpaUs,
    IReadOnlyList<WordExampleDto> Examples,
    ReviewRating Rating,
    double ScheduledDays,
    DateTime DueAtUtc,
    /// <summary>Ушла ли карточка на повтор в конец той же сессии.</summary>
    bool Requeued,
    StudyCardDto? NextCard,
    bool MatchedWithTypo = false);

// ─────────────────────────── Статистика ───────────────────────────

public sealed record StatsSummaryDto(
    int TotalCards,
    int LearnedCards,
    int CardsInProgress,
    int DueNow,
    int CurrentStreak,
    int LongestStreak,
    int ReviewedToday,
    int AccuracyLast30Days,
    /// <summary>Дневная цель в ответах: с ней сравнивается «ответов сегодня».</summary>
    int DailyGoal);

public sealed record DailyStatDto(
    DateOnly Date,
    int ReviewedCount,
    int NewWordsStarted,
    int CorrectCount,
    double? Accuracy);

public sealed record ActivityDto(
    DateOnly From,
    DateOnly To,
    IReadOnlyList<DailyStatDto> Days,
    /// <summary>Кривая удержания: доля правильных ответов по дням.</summary>
    IReadOnlyList<RetentionPointDto> Retention);

public sealed record RetentionPointDto(DateOnly Date, int Reviewed, int Correct, double Accuracy);

// ─────────────────────────── Профиль ───────────────────────────

/// <summary>Изменение ника и отображаемого имени.</summary>
/// <param name="Username">Новый ник или <c>null</c>, если менять не нужно.</param>
/// <param name="DisplayName">Новое отображаемое имя.</param>
public sealed record UpdateProfileRequestDto(string? Username, string? DisplayName);

/// <summary>Страница профиля: кто это и как у него дела.</summary>
public sealed record ProfileDto(
    UserDto User,
    UserSettingsDto Settings,
    int LearnedCards,
    int TotalCards,
    int CardsInProgress,
    int CurrentStreak,
    int LongestStreak,
    int FavoriteCount,
    int FriendCount,
    int ReviewedTotal);

/// <summary>Смена часового пояса.</summary>
/// <remarks>
/// Отдельный запрос, а не поле в общем обновлении настроек: пояс меняет смысл
/// уже накопленной дневной статистики и серии дней, поэтому это осознанное действие.
/// </remarks>
public sealed record UpdateTimeZoneRequestDto(string TimeZoneId);

/// <summary>
/// Профиль другого игрока: то, что видно друзьям.
/// </summary>
/// <remarks>
/// Отдельный DTO, а не <see cref="ProfileDto"/>: в своём профиле есть настройки,
/// почта и служебные поля, которых чужой видеть не должен. Здесь только прогресс —
/// ровно те числа, ради которых профиль друга и открывают.
/// </remarks>
/// <param name="MemberSince">Дата регистрации: по ней видно, кто в сервисе дольше.</param>
/// <param name="FriendsSince">Когда сложилась дружба. <c>null</c> — это свой профиль.</param>
public sealed record PublicProfileDto(
    string Username,
    string DisplayName,
    string? AvatarUrl,
    DateTime MemberSince,
    int LearnedCards,
    int CardsInProgress,
    int CurrentStreak,
    int LongestStreak,
    int ReviewedToday,
    int WeeklyWords,
    int WeeklyReviews,
    int ReviewedTotal,
    int FriendCount,
    DateTime? FriendsSince);

// ─────────────────────────── Избранное ───────────────────────────

/// <summary>Избранное слово вместе с прогрессом по нему.</summary>
/// <param name="Phase">Этап повторения. <c>null</c>, если слово ещё не начато.</param>
/// <param name="IsLearned">Интервал повторения превысил порог «выучено».</param>
/// <param name="AddedAt">Когда слово отметили избранным.</param>
public sealed record FavoriteWordDto(
    int WordId,
    string Lemma,
    string PartOfSpeech,
    string? IpaUk,
    string? IpaUs,
    string PrimaryTranslation,
    CefrLevel Level,
    CardPhase? Phase,
    double? IntervalDays,
    DateTime? DueAt,
    bool IsLearned,
    DateTime AddedAt);

/// <summary>Установка или снятие отметки «в избранном».</summary>
public sealed record SetFavoriteRequestDto(bool IsFavorite);

/// <summary>Состояние отметки после переключения.</summary>
public sealed record FavoriteStateDto(int WordId, bool IsFavorite);

/// <summary>Слово вместе с признаком избранного — для списков.</summary>
public sealed record WordWithFavoriteDto(WordDto Word, bool IsFavorite);

// ─────────────────────────── Друзья ───────────────────────────

/// <summary>Друг вместе с его прогрессом.</summary>
public sealed record FriendDto(
    string Username,
    string DisplayName,
    string? AvatarUrl,
    int LearnedCards,
    int CurrentStreak,
    int ReviewedToday,
    DateTime FriendsSince);

/// <summary>Заявка в друзья: входящая или исходящая.</summary>
public sealed record FriendRequestDto(string Username, string DisplayName, string? AvatarUrl, DateTime RequestedAt);

/// <summary>Страница друзей целиком: подтверждённые, входящие и исходящие заявки.</summary>
public sealed record FriendsOverviewDto(
    IReadOnlyList<FriendDto> Friends,
    IReadOnlyList<FriendRequestDto> Incoming,
    IReadOnlyList<FriendRequestDto> Outgoing);

/// <summary>Найденный человек и то, как он связан с текущим пользователем.</summary>
public sealed record UserSearchResultDto(
    string Username, string DisplayName, string? AvatarUrl, FriendRelation Relation);

/// <summary>Заявка в друзья по нику.</summary>
public sealed record AddFriendRequestDto(string Username);
