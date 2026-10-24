using EnglishTrainer.Domain.Entities;
using EnglishTrainer.Domain.Enums;

namespace EnglishTrainer.Domain.Repositories;

/// <summary>Хранилище слов и колод (общий справочник, не привязан к пользователю).</summary>
public interface IWordRepository
{
    Task<Word?> GetByIdAsync(int id, CancellationToken ct = default);

    /// <summary>Слово вместе с переводами и примерами — для карточки слова.</summary>
    Task<Word?> GetWithDetailsAsync(int id, CancellationToken ct = default);

    Task<Word?> GetByLemmaAsync(string lemma, string partOfSpeech, CancellationToken ct = default);

    /// <summary>
    /// Слова колоды. <paramref name="includeDetails"/> управляет загрузкой переводов
    /// и примеров: для списка они не нужны, для карточки обязательны.
    /// </summary>
    Task<IReadOnlyList<Word>> GetByDeckAsync(int deckId, bool includeDetails, CancellationToken ct = default);

    /// <summary>
    /// Слова по идентификаторам одним запросом, с переводами и примерами.
    /// Нужен, чтобы не делать N+1 при сборке очереди повторений.
    /// </summary>
    Task<IReadOnlyDictionary<int, Word>> GetWithDetailsByIdsAsync(
        IReadOnlyCollection<int> ids, CancellationToken ct = default);

    /// <summary>
    /// Слова, которых у пользователя ещё нет в изучении, в порядке полезности.
    /// Без колоды поиск идёт по всем колодам; с колодой — только по ней.
    /// Основа для отбора «новых» карточек в сессии.
    /// </summary>
    Task<IReadOnlyList<Word>> GetNewWordsForUserAsync(
        int userId, int? deckId, int limit, CancellationToken ct = default);

    /// <summary>
    /// Слова-соседи по колоде: нужны как правдоподобные неверные варианты
    /// в упражнении с выбором.
    /// </summary>
    Task<IReadOnlyList<Word>> GetDistractorsAsync(int excludeWordId, int limit, CancellationToken ct = default);

    /// <summary>Сколько слов в колоде — для расчёта прогресса.</summary>
    Task<int> CountInDeckAsync(int deckId, CancellationToken ct = default);

    /// <summary>Идентификаторы слов колоды — для подсчёта прогресса пользователя.</summary>
    Task<IReadOnlyList<int>> GetWordIdsInDeckAsync(int deckId, CancellationToken ct = default);

    /// <summary>Сколько всего слов в словаре.</summary>
    Task<int> CountAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Слово по порядковому номеру в словаре, вместе с переводами и примерами.
    /// </summary>
    /// <remarks>
    /// Нужно для «слова дня» у человека, который ещё ничего не начал учить:
    /// выбирать его из карточек нечем, а показать что-то полезное нужно.
    /// Порядок задан частотностью, поэтому смещение устойчиво между запусками.
    /// </remarks>
    Task<Word?> GetWithDetailsByOffsetAsync(int offset, CancellationToken ct = default);

    /// <summary>
    /// Поиск слов по началу написания или перевода.
    /// </summary>
    /// <remarks>
    /// Нужен, чтобы добавить в свою колоду уже существующее слово, а не заводить
    /// его второй раз: словарь общий, и «landscape» в нём должно остаться одним
    /// словом, сколько бы колод его ни включали.
    /// </remarks>
    Task<IReadOnlyList<Word>> SearchAsync(string query, int limit, CancellationToken ct = default);

    Task AddAsync(Word word, CancellationToken ct = default);

    Task AddRangeAsync(IEnumerable<Word> words, CancellationToken ct = default);
}

/// <summary>Хранилище колод.</summary>
public interface IDeckRepository
{
    /// <summary>
    /// Системные колоды, поставляемые с приложением.
    /// </summary>
    /// <remarks>
    /// Имя намеренно говорит «системные», а не «все»: личные колоды выбираются
    /// отдельно, потому что нужны далеко не везде. Например, новые слова берутся
    /// из самой приоритетной системной колоды — пополнять словарь из чужой
    /// личной колоды было бы странно.
    /// </remarks>
    Task<IReadOnlyList<Deck>> GetSystemAsync(CancellationToken ct = default);

    /// <summary>Системные колоды плюс личные колоды пользователя.</summary>
    Task<IReadOnlyList<Deck>> GetForUserAsync(int userId, CancellationToken ct = default);

    Task<Deck?> GetBySlugAsync(string slug, CancellationToken ct = default);

    Task<Deck?> GetByIdAsync(int id, CancellationToken ct = default);

    Task<bool> SlugExistsAsync(string slug, CancellationToken ct = default);

    Task AddAsync(Deck deck, CancellationToken ct = default);

    Task RemoveAsync(Deck deck, CancellationToken ct = default);

    /// <summary>Связь «слово в колоде», если она есть.</summary>
    Task<DeckWord?> GetWordLinkAsync(int deckId, int wordId, CancellationToken ct = default);

    Task AddWordAsync(DeckWord link, CancellationToken ct = default);

    Task RemoveWordAsync(DeckWord link, CancellationToken ct = default);

    /// <summary>Следующий порядковый номер в колоде — новое слово идёт в конец.</summary>
    Task<int> NextWordOrderAsync(int deckId, CancellationToken ct = default);

    /// <summary>
    /// Сколько колод пользователь выучил целиком.
    /// </summary>
    /// <param name="learnedIntervalDays">
    /// Порог «выучено» из <c>CardState.LearnedIntervalDays</c>. Передаётся
    /// параметром, а не берётся здесь: правило принадлежит домену, и это
    /// единственный способ не завести вторую копию числа.
    /// </param>
    /// <remarks>
    /// Отдельный запрос, а не пересчёт прогресса по всем колодам: страница
    /// достижений обращается к этому показателю, а перебор 18 колод с отдельным
    /// запросом на каждую — это три с лишним десятка обращений к базе ради
    /// одного числа.
    /// </remarks>
    Task<int> CountCompletedAsync(
        int userId, double learnedIntervalDays, CancellationToken ct = default);
}

/// <summary>Хранилище пользователей.</summary>
public interface IUserRepository
{
    Task<User?> GetByIdAsync(int id, CancellationToken ct = default);

    Task<User?> GetByPublicIdAsync(Guid publicId, CancellationToken ct = default);

    Task<User?> GetByEmailAsync(string normalizedEmail, CancellationToken ct = default);

    Task<User?> GetByUsernameAsync(string username, CancellationToken ct = default);

    Task<User?> GetByTelegramIdAsync(long telegramId, CancellationToken ct = default);

    /// <summary>Пользователь вместе с настройками — типовой сценарий для API.</summary>
    Task<User?> GetWithSettingsAsync(int userId, CancellationToken ct = default);

    Task AddAsync(User user, CancellationToken ct = default);

    Task<bool> EmailExistsAsync(string normalizedEmail, CancellationToken ct = default);

    Task<bool> UsernameExistsAsync(string username, CancellationToken ct = default);

    /// <summary>
    /// Поиск людей по началу ника — чтобы добавить в друзья, нужно сначала найти.
    /// Совпадение ищется с начала строки: так работает индекс по нику.
    /// </summary>
    Task<IReadOnlyList<User>> SearchByUsernameAsync(string query, int limit, CancellationToken ct = default);

    /// <summary>
    /// Пользователи, которым пора отправить напоминание в Telegram.
    /// </summary>
    /// <remarks>
    /// Отбор идёт по сохранённому моменту следующего напоминания, а не по
    /// сравнению локального времени каждого пользователя с его настройками:
    /// во втором случае планировщик читал бы таблицу пользователей целиком
    /// каждую минуту, а в первом — только строки по индексу.
    /// </remarks>
    Task<IReadOnlyList<User>> GetDueRemindersAsync(
        DateTime nowUtc, int limit, CancellationToken ct = default);
}

/// <summary>Хранилище избранных слов.</summary>
public interface IFavoriteWordRepository
{
    /// <summary>Идентификаторы избранных слов пользователя — для отметок в интерфейсе.</summary>
    Task<IReadOnlyCollection<int>> GetWordIdsAsync(int userId, CancellationToken ct = default);

    Task<bool> ExistsAsync(int userId, int wordId, CancellationToken ct = default);

    Task AddAsync(FavoriteWord favorite, CancellationToken ct = default);

    /// <summary>Убирает слово из избранного. Возвращает <c>false</c>, если его там не было.</summary>
    Task<bool> RemoveAsync(int userId, int wordId, CancellationToken ct = default);

    /// <summary>Избранные слова вместе со словом и состоянием повторения.</summary>
    Task<IReadOnlyList<FavoriteWord>> GetWithWordsAsync(int userId, CancellationToken ct = default);

    Task<int> CountAsync(int userId, CancellationToken ct = default);
}

/// <summary>Хранилище заявок в друзья.</summary>
public interface IFriendshipRepository
{
    /// <summary>Заявка между двумя пользователями в любом направлении.</summary>
    Task<Friendship?> GetBetweenAsync(int firstUserId, int secondUserId, CancellationToken ct = default);

    Task AddAsync(Friendship friendship, CancellationToken ct = default);

    Task RemoveAsync(Friendship friendship, CancellationToken ct = default);

    /// <summary>Принятые друзья. Оба пользователя загружаются сразу.</summary>
    Task<IReadOnlyList<Friendship>> GetAcceptedAsync(int userId, CancellationToken ct = default);

    /// <summary>Входящие заявки, ожидающие ответа.</summary>
    Task<IReadOnlyList<Friendship>> GetIncomingAsync(int userId, CancellationToken ct = default);

    /// <summary>Исходящие заявки, ожидающие ответа.</summary>
    Task<IReadOnlyList<Friendship>> GetOutgoingAsync(int userId, CancellationToken ct = default);

    Task<int> CountAcceptedAsync(int userId, CancellationToken ct = default);
}

/// <summary>Хранилище состояний повторения.</summary>
public interface ICardStateRepository
{
    Task<CardState?> GetAsync(int userId, int wordId, CancellationToken ct = default);

    /// <summary>
    /// Карточки, которые пора повторить, — самые просроченные первыми.
    /// Это самый горячий запрос системы: под него нужен индекс (UserId, DueAt).
    /// </summary>
    /// <param name="deckId">
    /// Колода, если человек выбрал её явно. Без ограничения по колоде очередь
    /// «по колоде B» состояла бы из слов колоды A: долг по повторениям общий
    /// для пользователя, а не для колоды.
    /// </param>
    Task<IReadOnlyList<CardState>> GetDueAsync(
        int userId, DateTime nowUtc, int limit, int? deckId = null, CancellationToken ct = default);

    Task<int> CountDueAsync(int userId, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>
    /// Ближайший срок повторения в будущем, если прямо сейчас повторять нечего.
    /// </summary>
    /// <remarks>
    /// Нужен, чтобы ответ «карточек нет» был содержательным: человеку важно знать,
    /// ждать минуту или сутки.
    /// </remarks>
    Task<DateTime?> GetNextDueAtAsync(int userId, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>
    /// Карточки с самым близким сроком, включая ещё не наступивший.
    /// </summary>
    /// <remarks>
    /// Для тренировки вне расписания. Интервалы специально разносят повторения
    /// во времени, но человеку иногда нужно заниматься именно сейчас, и запрещать
    /// ему это было бы странно.
    /// </remarks>
    /// <param name="deckId">
    /// Колода, если человек выбрал её явно: «заниматься этой колодой» не должно
    /// означать «получить слова другой».
    /// </param>
    Task<IReadOnlyList<CardState>> GetSoonestAsync(
        int userId, int limit, int? deckId = null, CancellationToken ct = default);

    Task<IReadOnlyList<CardState>> GetForWordsAsync(
        int userId, IReadOnlyCollection<int> wordIds, CancellationToken ct = default);

    Task AddAsync(CardState cardState, CancellationToken ct = default);

    Task AddRangeAsync(IEnumerable<CardState> cardStates, CancellationToken ct = default);

    /// <summary>Сводка по всем карточкам пользователя для страницы прогресса.</summary>
    Task<CardStateSummary> GetSummaryAsync(int userId, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>
    /// Карточки для отдельной страницы слов: «в работе», «пора повторить» или
    /// «выучено». Порядок — от ближайшего срока к дальнему; у выученных —
    /// по времени последнего повторения.
    /// </summary>
    Task<IReadOnlyList<CardState>> GetProgressAsync(
        int userId, WordProgressScope scope, DateTime nowUtc, CancellationToken ct = default);
}

/// <summary>Агрегированные показатели прогресса пользователя.</summary>
/// <param name="Total">Всего карточек в изучении.</param>
/// <param name="Learned">Выучено (интервал больше месяца).</param>
/// <param name="DueNow">Пора повторить прямо сейчас.</param>
public readonly record struct CardStateSummary(int Total, int Learned, int DueNow);

/// <summary>Хранилище журнала ответов.</summary>
public interface IReviewLogRepository
{
    Task AddAsync(ReviewLog log, CancellationToken ct = default);

    /// <summary>Сколько всего ответов дал пользователь за всё время.</summary>
    Task<int> CountAsync(int userId, CancellationToken ct = default);

    /// <summary>История по слову — для карточки «как я учил это слово».</summary>
    Task<IReadOnlyList<ReviewLog>> GetForWordAsync(int userId, int wordId, CancellationToken ct = default);

    /// <summary>
    /// Кривая удержания за период: сколько ответов и сколько из них правильные.
    /// Единственный честный показатель того, работает ли методика.
    /// </summary>
    Task<IReadOnlyList<RetentionPoint>> GetRetentionAsync(
        int userId, DateTime fromUtc, CancellationToken ct = default);
}

/// <summary>Точка кривой удержания за день.</summary>
public readonly record struct RetentionPoint(DateOnly Date, int Reviewed, int Correct);

/// <summary>Хранилище сессий обучения.</summary>
public interface IStudySessionRepository
{
    /// <summary>Активная сессия пользователя, если есть.</summary>
    Task<StudySession?> GetActiveAsync(int userId, CancellationToken ct = default);

    /// <summary>Активная сессия вместе с очередью, словами и переводами — для отдачи в API.</summary>
    Task<StudySession?> GetActiveWithItemsAsync(int userId, CancellationToken ct = default);

    Task AddAsync(StudySession session, CancellationToken ct = default);
}

/// <summary>Хранилище дневной статистики.</summary>
public interface IDailyStatRepository
{
    Task<DailyStat?> GetAsync(int userId, DateOnly date, CancellationToken ct = default);

    Task<IReadOnlyList<DailyStat>> GetRangeAsync(
        int userId, DateOnly from, DateOnly to, CancellationToken ct = default);

    Task AddAsync(DailyStat stat, CancellationToken ct = default);

    /// <summary>Последние <paramref name="days"/> дней, свежие первыми — для серии дней.</summary>
    Task<IReadOnlyList<DailyStat>> GetRecentAsync(int userId, int days, CancellationToken ct = default);

    /// <summary>
    /// Дневная статистика нескольких пользователей за период.
    /// </summary>
    /// <remarks>
    /// Отдельный метод ради рейтинга: собрать статистику десяти друзей по
    /// одному запросу на каждого — это десять обращений к базе там, где
    /// хватает одного. Период задаётся диапазоном, потому что недельное окно
    /// у каждого участника своё: считается оно в его часовом поясе.
    /// </remarks>
    Task<IReadOnlyList<DailyStat>> GetRangeForUsersAsync(
        IReadOnlyCollection<int> userIds, DateOnly from, DateOnly to, CancellationToken ct = default);
}

/// <summary>Хранилище токенов сброса пароля.</summary>
public interface IPasswordResetTokenRepository
{
    /// <summary>
    /// Токен по хешу. Возвращается отслеживаемым: при использовании меняется
    /// его состояние, и это изменение должно попасть в базу.
    /// </summary>
    Task<PasswordResetToken?> GetByHashAsync(string tokenHash, CancellationToken ct = default);

    /// <summary>Действующие токены пользователя — нужны, чтобы погасить их после сброса.</summary>
    Task<IReadOnlyList<PasswordResetToken>> GetActiveAsync(
        int userId, DateTime nowUtc, CancellationToken ct = default);

    Task AddAsync(PasswordResetToken token, CancellationToken ct = default);
}

/// <summary>Хранилище кодов привязки Telegram.</summary>
public interface ITelegramLinkCodeRepository
{
    /// <summary>
    /// Код по хешу. Возвращается отслеживаемым: при использовании меняется
    /// его состояние, и это изменение должно попасть в базу.
    /// </summary>
    Task<TelegramLinkCode?> GetByHashAsync(string codeHash, CancellationToken ct = default);

    /// <summary>Действующие коды пользователя — нужны, чтобы погасить их при новой привязке.</summary>
    Task<IReadOnlyList<TelegramLinkCode>> GetActiveAsync(
        int userId, DateTime nowUtc, CancellationToken ct = default);

    Task AddAsync(TelegramLinkCode code, CancellationToken ct = default);
}

/// <summary>Хранилище полученных достижений.</summary>
public interface IUserAchievementRepository
{
    /// <summary>
    /// Коды полученных достижений.
    /// </summary>
    /// <remarks>
    /// Отдельный метод для кодов, а не чтение всех строк: проверка достижений
    /// идёт после каждого ответа, и ей нужно только «что уже есть» — несколько
    /// коротких строк вместо всей истории с датами.
    /// </remarks>
    Task<IReadOnlyCollection<string>> GetCodesAsync(int userId, CancellationToken ct = default);

    /// <summary>Полученные достижения с датами — для страницы достижений.</summary>
    Task<IReadOnlyList<UserAchievement>> GetAsync(int userId, CancellationToken ct = default);

    /// <summary>
    /// Выдаёт достижения, пропуская уже выданные.
    /// </summary>
    /// <returns>Сколько достижений выдано на самом деле.</returns>
    /// <remarks>
    /// Идемпотентность обеспечивает база, а не проверка перед вставкой: между
    /// проверкой и вставкой есть окно, и два одновременных запроса его
    /// используют. Повторная выдача награды — не ошибка, а нормальный исход
    /// гонки, поэтому и метод её не считает ошибкой.
    /// </remarks>
    Task<int> UnlockAsync(
        int userId, IReadOnlyCollection<string> codes, DateTime unlockedAt, CancellationToken ct = default);
}

/// <summary>
/// Единица работы. Нужна, чтобы Application-слой завершал транзакцию,
/// не зная ничего про DbContext и SaveChanges.
/// </summary>
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
