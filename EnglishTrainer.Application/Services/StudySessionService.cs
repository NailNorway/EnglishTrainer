using EnglishTrainer.Application.Dtos;
using EnglishTrainer.Application.Mapping;
using EnglishTrainer.Application.Text;
using EnglishTrainer.Domain.Abstractions;
using EnglishTrainer.Domain.Entities;
using EnglishTrainer.Domain.Enums;
using EnglishTrainer.Domain.Exceptions;
using EnglishTrainer.Domain.Repositories;
using EnglishTrainer.Domain.Scheduling;

namespace EnglishTrainer.Application.Services;

/// <summary>
/// Сборка очереди карточек и проверка ответов — ядро продукта.
/// </summary>
/// <remarks>
/// <para>
/// Здесь принимаются три решения, от которых зависит, будет ли сервис полезен:
/// сколько повторений дать, сколько новых слов ввести и в каком порядке всё это показать.
/// </para>
/// <para>
/// Очередь <b>сохраняется в БД</b>, а не собирается на каждый запрос: при обновлении
/// страницы или переходе на другое устройство пользователь должен продолжить ту же
/// сессию, а не получить новый набор слов.
/// </para>
/// </remarks>
public sealed class StudySessionService(
    IUserRepository users,
    IWordRepository words,
    ICardStateRepository cardStates,
    IFavoriteWordRepository favorites,
    IStudySessionRepository sessions,
    IReviewLogRepository reviewLogs,
    IDailyStatRepository dailyStats,
    IDeckRepository decks,
    ISrsScheduler scheduler,
    IClock clock,
    IUnitOfWork unitOfWork)
{
    /// <summary>Сколько вариантов ответа показывать в упражнении с выбором.</summary>
    private const int ChoiceOptionCount = 4;

    /// <summary>Сколько примеров возвращать в разборе ответа.</summary>
    private const int MaxExamplesInResult = 3;

    /// <summary>Код языка вопроса, когда вопрос задан по-английски.</summary>
    private const string PromptLanguageEnglish = "en";

    /// <summary>Код языка вопроса, когда вопрос задан переводом.</summary>
    private const string PromptLanguageRussian = "ru";

    /// <summary>
    /// Возвращает активную сессию, а если её нет — собирает новую.
    /// </summary>
    /// <remarks>
    /// Продолжается только та сессия, которая отвечает намерению запроса. Если
    /// человек выбрал другую колоду, старая сессия закрывается: держать две
    /// очереди одновременно нельзя (это же правило действует для избранного),
    /// а показать вместо выбранной колоды чужую — хуже, чем потерять очередь,
    /// которая всё равно восстанавливается из состояний карточек.
    /// </remarks>
    public async Task<StudySessionDto> GetOrCreateSessionAsync(
        int userId, int? deckId, bool cram = false, bool onlyDue = false, CancellationToken ct = default)
    {
        var user = await users.GetWithSettingsAsync(userId, ct)
            ?? throw NotFoundException.For("Пользователь", userId);

        var existing = await sessions.GetActiveWithItemsAsync(userId, ct);

        if (existing is not null)
        {
            // Колода не задана — значит, запрошена общая тренировка, и подходит
            // любая активная очередь. Колода задана — подходит только своя.
            var sameDeck = deckId is null || existing.DeckId == deckId;

            // Отвеченная очередь — та же ловушка, что и пустая: возвращать её
            // значит показывать «сессия закончена» на каждый запрос.
            if (sameDeck && existing.Items.Any(item => !item.IsAnswered))
                return await BuildSessionDtoAsync(existing, user, ct);

            existing.Status = StudySessionStatus.Abandoned;
            existing.CompletedAt = clock.UtcNow;
            await unitOfWork.SaveChangesAsync(ct);
        }

        return await CreateSessionAsync(user, deckId, cram, onlyDue, ct);
    }

    private async Task<StudySessionDto> CreateSessionAsync(
        User user, int? deckId, bool cram, bool onlyDue, CancellationToken ct)
    {
        var userId = user.Id;
        var settings = user.Settings ?? new UserSettings();
        var nowUtc = clock.UtcNow;
        var localDate = clock.TodayFor(user.TimeZoneId);

        if (deckId is not null && await decks.GetByIdAsync(deckId.Value, ct) is null)
            throw NotFoundException.For("Колода", deckId.Value);

        // 1. Повторения: самые просроченные первыми — они «горячие», их забывают быстрее.
        // Вне расписания берутся карточки с самым близким сроком, даже если он ещё
        // не наступил: это осознанное желание заниматься сейчас, а не сбой алгоритма,
        // и интервалы при ответе всё равно пересчитаются.
        //
        // Выбранная колода ограничивает и повторения: «заниматься колодой B» не
        // должно означать «получить слова колоды A».
        var due = cram
            ? await cardStates.GetSoonestAsync(userId, settings.MaxCardsPerSession, deckId, ct)
            : await cardStates.GetDueAsync(userId, nowUtc, settings.MaxCardsPerSession, deckId, ct);

        // Избранные слова поднимаются в начало очереди. Алгоритм расставляет
        // приоритеты по вероятности забывания, но не знает, что завтра собеседование
        // или визит к врачу, — а человек это знает и отметил нужные слова.
        var favoriteIds = await favorites.GetWordIdsAsync(userId, ct);
        var orderedDue = due
            .OrderByDescending(card => favoriteIds.Contains(card.WordId))
            .ThenBy(card => card.DueAt)
            .ToList();

        // Слова к повторениям подтягиваем одним запросом, а не по одному на карточку.
        var dueWords = await words.GetWithDetailsByIdsAsync(orderedDue.Select(c => c.WordId).ToArray(), ct);

        var session = new StudySession
        {
            UserId = userId,
            DeckId = deckId,
            Status = StudySessionStatus.Active,
            StartedAt = nowUtc,
            LocalDate = localDate,
            PlannedReviewCount = orderedDue.Count
        };

        var position = 1;

        foreach (var card in orderedDue)
        {
            if (!dueWords.TryGetValue(card.WordId, out var word))
                continue;

            session.Items.Add(new StudySessionItem
            {
                WordId = word.Id,
                CardStateId = card.Id,
                Position = position++,
                Kind = SessionItemKind.Review,
                Exercise = PickExercise(card.Phase, word.Id, userId)
            });
        }

        // 2. Новые слова в пределах дневного лимита. Вне расписания и на странице
        // «Пора повторить» они не подмешиваются: это отдельные намерения, и они
        // не должны расходовать дневной лимит.
        IReadOnlyList<Word> newWords = cram || onlyDue
            ? []
            : await SelectNewWordsAsync(user, settings, localDate, deckId, ct);

        // Пустую сессию не сохраняем.
        //
        // Сессия без единой карточки ничего не значит, но, оставшись активной, она
        // перекрывает все последующие запуски: следующий запрос вернул бы её же,
        // интерфейс увидел бы «карточек нет» и снова показал «сессия закончена».
        // Вместо записи в базу отвечаем состоянием «сейчас повторять нечего».
        if (session.Items.Count == 0 && newWords.Count == 0)
            return await BuildNothingToStudyAsync(user, ct);

        var newCards = new List<CardState>();
        foreach (var word in newWords)
        {
            // Новое слово сразу получает состояние карточки. Иначе завтра оно
            // снова окажется «новым», дневной лимит будет тратиться на него повторно,
            // а счётчик выученного никогда не сдвинется.
            var fresh = new CardState
            {
                UserId = userId,
                WordId = word.Id,
                Phase = CardPhase.New,
                Stability = 0,
                Difficulty = 0,
                IntervalDays = 0,
                DueAt = nowUtc
            };

            newCards.Add(fresh);
            await cardStates.AddAsync(fresh, ct);
        }

        // Сохраняем состояния до создания элементов: нужны их идентификаторы.
        await unitOfWork.SaveChangesAsync(ct);

        foreach (var (word, card) in newWords.Zip(newCards))
        {
            session.Items.Add(new StudySessionItem
            {
                WordId = word.Id,
                CardStateId = card.Id,
                Position = position++,
                Kind = SessionItemKind.New,
                Exercise = ExerciseKind.Typing
            });
        }

        session.PlannedNewCount = newCards.Count;

        await sessions.AddAsync(session, ct);
        await unitOfWork.SaveChangesAsync(ct);

        // Слова для отдачи в DTO подтягиваем уже по сохранённой очереди.
        var created = await sessions.GetActiveWithItemsAsync(userId, ct)
            ?? throw new InvalidOperationException("Сессия не была сохранена.");

        return await BuildSessionDtoAsync(created, user, ct);
    }

    /// <summary>
    /// Активная сессия, если она есть. Ничего не создаёт и не расходует дневной лимит.
    /// </summary>
    /// <remarks>
    /// Нужен интерфейсу: список колод показывает «продолжить сессию», но вызывать
    /// <see cref="GetOrCreateSessionAsync"/> для этого нельзя — он создаст новую
    /// сессию и вычтет новые слова из дневного лимита, даже если человек просто
    /// открыл страницу и ушёл.
    /// </remarks>
    public async Task<StudySessionDto?> GetActiveSessionAsync(int userId, CancellationToken ct = default)
    {
        var user = await users.GetWithSettingsAsync(userId, ct)
            ?? throw NotFoundException.For("Пользователь", userId);

        var existing = await sessions.GetActiveWithItemsAsync(userId, ct);

        return existing is null ? null : await BuildSessionDtoAsync(existing, user, ct);
    }

    /// <summary>
    /// Тренировка только по избранным словам.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Отличий от обычной сессии два. Первое: избранные слова не ждут назначенного
    /// срока — иначе «повторить перед собеседованием» было бы невозможно, ведь
    /// алгоритм специально разносит повторения во времени.
    /// </para>
    /// <para>
    /// Второе: текущая активная сессия закрывается. Пользователь осознанно выбрал
    /// другую тренировку, и держать две очереди одновременно нельзя — состояние
    /// «одна активная сессия» защищено индексом в базе. Прогресс не теряется:
    /// он хранится в состояниях карточек, а не в очереди.
    /// </para>
    /// </remarks>
    public async Task<StudySessionDto> StartFavoritesSessionAsync(int userId, CancellationToken ct = default)
    {
        var user = await users.GetWithSettingsAsync(userId, ct)
            ?? throw NotFoundException.For("Пользователь", userId);

        var favoriteIds = await favorites.GetWordIdsAsync(userId, ct);

        if (favoriteIds.Count == 0)
        {
            throw new ConflictException(
                "В избранном пока нет слов. Отметьте слова звёздочкой — и они появятся здесь.");
        }

        var active = await sessions.GetActiveAsync(userId, ct);

        if (active is not null)
        {
            active.Status = StudySessionStatus.Abandoned;
            active.CompletedAt = clock.UtcNow;
            await unitOfWork.SaveChangesAsync(ct);
        }

        var settings = user.Settings ?? new UserSettings();
        var nowUtc = clock.UtcNow;

        var details = await words.GetWithDetailsByIdsAsync(favoriteIds, ct);
        var states = await cardStates.GetForWordsAsync(userId, favoriteIds, ct);
        var statesByWord = states.ToDictionary(state => state.WordId);

        // Сначала те, у кого срок уже подошёл: они «горячее». Слова без состояния
        // (отмеченные, но ещё не начатые) идут в конец — их всё равно придётся учить с нуля.
        var ordered = details.Values
            .OrderBy(word => statesByWord.TryGetValue(word.Id, out var state) ? state.DueAt : DateTime.MaxValue)
            .ThenBy(word => word.Id)
            .Take(settings.MaxCardsPerSession)
            .ToList();

        var session = new StudySession
        {
            UserId = userId,
            DeckId = null,
            Status = StudySessionStatus.Active,
            StartedAt = nowUtc,
            LocalDate = clock.TodayFor(user.TimeZoneId),
            PlannedReviewCount = ordered.Count
        };

        var position = 1;

        foreach (var word in ordered)
        {
            statesByWord.TryGetValue(word.Id, out var state);

            session.Items.Add(new StudySessionItem
            {
                WordId = word.Id,
                CardStateId = state?.Id,
                Position = position++,
                Kind = SessionItemKind.Favorite,
                Exercise = PickExercise(state?.Phase ?? CardPhase.New, word.Id, userId)
            });
        }

        await sessions.AddAsync(session, ct);
        await unitOfWork.SaveChangesAsync(ct);

        var created = await sessions.GetActiveWithItemsAsync(userId, ct)
            ?? throw new InvalidOperationException("Сессия не была сохранена.");

        return await BuildSessionDtoAsync(created, user, ct);
    }

    /// <summary>
    /// Отбирает новые слова: только те, что ещё не в изучении, и не больше
    /// остатка дневного лимита.
    /// </summary>
    /// <remarks>
    /// Лимит ограничивает автоматический подбор, но не осознанный выбор: если
    /// человек открыл конкретную колоду, он просит именно её слова, и ответ
    /// «на сегодня всё» после первого же прогона выглядел бы поломкой. Введённые
    /// слова при этом честно попадают в дневную статистику, поэтому обычная
    /// тренировка в тот же день новых слов уже не предложит.
    ///
    /// Без выбранной колоды слова берутся из всех колод, а не только из первой
    /// системной. Раньше общая тренировка замыкалась на «Первых днях»: после
    /// прохождения этой колоды новые слова переставали приходить, хотя в других
    /// колодах их было ещё много.
    /// </remarks>
    private async Task<IReadOnlyList<Word>> SelectNewWordsAsync(
        User user, UserSettings settings, DateOnly localDate, int? deckId, CancellationToken ct)
    {
        if (settings.NewWordsPerDay <= 0)
            return [];

        var chosenDeck = deckId is not null;

        var todayStat = await dailyStats.GetAsync(user.Id, localDate, ct);
        var alreadyStarted = todayStat?.NewWordsStarted ?? 0;
        var remaining = settings.NewWordsPerDay - alreadyStarted;

        if (!chosenDeck && remaining <= 0)
            return [];

        // У выбранной колоды остаток лимита не спрашивается, но и целая колода
        // за один заход не выдаётся: за раз вводится обычная дневная норма новых
        // слов. Иначе выбор темы превращался бы в «получить все её слова сразу» —
        // ровно то выгорание, от которого лимит и защищает.
        var limit = chosenDeck ? settings.NewWordsPerDay : remaining;

        var candidates = await words.GetNewWordsForUserAsync(user.Id, deckId, limit, ct);

        var stat = todayStat ?? new DailyStat { UserId = user.Id, Date = localDate };
        stat.NewWordsStarted += candidates.Count;

        if (todayStat is null)
            await dailyStats.AddAsync(stat, ct);

        return candidates;
    }

    /// <summary>
    /// Проверяет ответ и пересчитывает интервал повторения.
    /// </summary>
    public async Task<AnswerResultDto> SubmitAnswerAsync(
        int userId, SubmitAnswerRequestDto request, CancellationToken ct = default)
    {
        var user = await users.GetWithSettingsAsync(userId, ct)
            ?? throw NotFoundException.For("Пользователь", userId);

        var session = await sessions.GetActiveWithItemsAsync(userId, ct)
            ?? throw new ConflictException("Активной сессии нет. Начните новую.");

        var item = session.Items.FirstOrDefault(i => i.Id == request.ItemId)
            ?? throw NotFoundException.For("Карточка сессии", request.ItemId);

        if (item.IsAnswered)
            throw new ConflictException("На эту карточку уже был дан ответ.");

        var word = item.Word
            ?? throw new InvalidOperationException("Слово не загружено вместе с элементом сессии.");

        var accepted = AcceptedAnswers(word);
        var match = AnswerNormalizer.Compare(request.Answer, accepted);
        var isCorrect = match.IsMatch;

        // Оценку можно прислать явно (пользователь нажал «легко»), но по умолчанию
        // она выводится из правильности — иначе данные для алгоритма будут случайными.
        var rating = request.Rating ?? (isCorrect ? ReviewRating.Good : ReviewRating.Again);

        var nowUtc = clock.UtcNow;
        var cardState = item.CardState;

        if (cardState is null)
        {
            cardState = await cardStates.GetAsync(userId, word.Id, ct);
        }

        var elapsedDays = cardState?.LastReviewedAt is { } lastReviewed
            ? Math.Max(0, (nowUtc - lastReviewed).TotalDays)
            : 0;

        var result = scheduler.Schedule(cardState, rating, nowUtc);

        if (cardState is null)
        {
            cardState = new CardState { UserId = userId, WordId = word.Id };
            await cardStates.AddAsync(cardState, ct);
            item.CardState = cardState;
        }

        await reviewLogs.AddAsync(new ReviewLog
        {
            CardState = cardState,
            UserId = userId,
            WordId = word.Id,
            Rating = rating,
            ReviewedAt = nowUtc,
            ElapsedDays = elapsedDays,
            ScheduledDays = result.IntervalDays,
            PhaseBefore = cardState.Phase,
            StabilityBefore = cardState.Stability,
            DifficultyBefore = cardState.Difficulty,
            ElapsedMs = request.ElapsedMs,
            Exercise = item.Exercise
        }, ct);

        cardState.Phase = result.Phase;
        cardState.Stability = result.Stability;
        cardState.Difficulty = result.Difficulty;
        cardState.IntervalDays = result.IntervalDays;
        cardState.DueAt = result.DueAt;
        cardState.Reps++;
        cardState.LastReviewedAt = nowUtc;

        if (result.IsLapse)
            cardState.Lapses++;

        item.IsAnswered = true;
        item.AnsweredAt = nowUtc;
        item.Rating = rating;

        session.AnsweredCount++;
        if (isCorrect)
            session.CorrectCount++;

        // «Не помню» → карточка возвращается в конец этой же сессии, пока слово свежо
        // в памяти. Это закрепляет его лучше, чем отложенное повторение через час.
        var requeued = false;
        if (rating == ReviewRating.Again && session.Items.Count < 500)
        {
            session.Items.Add(new StudySessionItem
            {
                StudySessionId = session.Id,
                WordId = word.Id,
                CardStateId = cardState.Id,
                Position = session.Items.Max(i => i.Position) + 1,
                Kind = item.Kind,
                Exercise = ExerciseKind.Typing,
                IsRequeued = true
            });

            requeued = true;
        }

        await UpdateDailyAdHocAsync(user, session.LocalDate, isCorrect, ct);

        var remainingBeforeSave = session.Items.Count(i => !i.IsAnswered);
        if (remainingBeforeSave == 0)
        {
            session.Status = StudySessionStatus.Completed;
            session.CompletedAt = nowUtc;
        }

        await unitOfWork.SaveChangesAsync(ct);

        var nextItem = session.Items
            .Where(i => !i.IsAnswered)
            .OrderBy(i => i.Position)
            .FirstOrDefault();

        var nextCard = nextItem is null
            ? null
            : await BuildCardAsync(nextItem, userId, ct);

        return new AnswerResultDto(
            isCorrect,
            // Эталон для показа — английское слово: цель сервиса в том,
            // чтобы человек запоминал английский, а не русский перевод.
            word.Lemma,
            word.IpaUk,
            word.IpaUs,
            word.Examples.Take(MaxExamplesInResult).Select(e => e.ToDto()).ToList(),
            rating,
            result.IntervalDays,
            result.DueAt,
            requeued,
            nextCard,
            // Опечатка не отменяет балл, но человек должен увидеть, как слово
            // пишется: иначе он закрепит именно свою ошибку.
            match.IsApproximate);
    }

    /// <summary>Помечает незавершённую сессию брошенной, чтобы она не блокировала новую.</summary>
    public async Task AbandonSessionAsync(int userId, CancellationToken ct = default)
    {
        var session = await sessions.GetActiveAsync(userId, ct);
        if (session is null)
            return;

        session.Status = StudySessionStatus.Abandoned;
        session.CompletedAt = clock.UtcNow;
        await unitOfWork.SaveChangesAsync(ct);
    }

    /// <summary>Принятые варианты ответа. Первый элемент — эталон для показа.</summary>
    private static List<string> AcceptedAnswers(Word word, ExerciseKind exercise)
    {
        var translations = word.Translations
            .OrderByDescending(t => t.IsPrimary)
            .Select(t => t.Text)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();

        return exercise switch
        {
            // При выборе из вариантов принимаем и слово, и перевод:
            // пользователь может ответить в любую сторону.
            ExerciseKind.MultipleChoice => [.. translations, word.Lemma],
            // При вводе английского слова ответом считается только само слово.
            ExerciseKind.Spelling or ExerciseKind.Listening => [word.Lemma],
            _ => [.. translations, word.Lemma]
        };
    }

    /// <summary>
    /// Выбор упражнения. Детерминированный: одно и то же слово в одной сессии
    /// не должно внезапно менять тип задания при обновлении страницы.
    /// </summary>
    private static ExerciseKind PickExercise(CardPhase phase, int wordId, int userId)
    {
        var mixed = unchecked(wordId * 397 ^ userId * 17);

        return phase switch
        {
            CardPhase.New => ExerciseKind.Typing,
            CardPhase.Review or CardPhase.Relearning => (Math.Abs(mixed) % 3) switch
            {
                0 => ExerciseKind.MultipleChoice,
                1 => ExerciseKind.Typing,
                _ => ExerciseKind.Listening
            },
            _ => ExerciseKind.Typing
        };
    }

    /// <summary>
    /// Ответ на запрос тренировки, когда повторять нечего.
    /// </summary>
    /// <remarks>
    /// Сессия намеренно не создаётся: хранить пустую очередь нечего, а её наличие
    /// ломало бы последующие запуски. Вместо этого отдаётся ближайший срок
    /// повторения — интерфейсу нужно уметь сказать «на сегодня всё, следующее
    /// повторение в 18:40». Это не то же самое, что «сессия закончена»:
    /// во втором случае человек поработал, а в первом ещё ничего не сделал.
    /// </remarks>
    private async Task<StudySessionDto> BuildNothingToStudyAsync(User user, CancellationToken ct)
    {
        var nextDueAt = await cardStates.GetNextDueAtAsync(user.Id, clock.UtcNow, ct);

        return new StudySessionDto(
            SessionId: 0,
            Status: StudySessionStatus.Active,
            CurrentCard: null,
            Position: 0,
            TotalItems: 0,
            Remaining: 0,
            DueCount: 0,
            NewCount: 0,
            LocalDate: clock.TodayFor(user.TimeZoneId),
            NextDueAtUtc: nextDueAt);
    }

    private async Task<StudySessionDto> BuildSessionDtoAsync(
        StudySession session, User user, CancellationToken ct)
    {
        var current = session.Items
            .Where(i => !i.IsAnswered)
            .OrderBy(i => i.Position)
            .FirstOrDefault();

        var card = current is null ? null : await BuildCardAsync(current, user.Id, ct);

        return new StudySessionDto(
            session.Id,
            session.Status,
            card,
            card?.Position ?? session.Items.Count,
            session.Items.Count,
            session.Items.Count(i => !i.IsAnswered),
            session.PlannedReviewCount,
            session.PlannedNewCount,
            session.LocalDate);
    }

    private async Task<StudyCardDto> BuildCardAsync(StudySessionItem item, int userId, CancellationToken ct)
    {
        var word = item.Word
            ?? throw new InvalidOperationException("Слово не загружено вместе с элементом сессии.");

        var audioUrl = FindAudioUrl(word);
        var exercise = item.Exercise;

        if (exercise == ExerciseKind.MultipleChoice)
        {
            var options = await BuildOptionsAsync(word, item.WordId, userId, ct);
            if (options.Count >= 2)
            {
                // Вопрос — английское слово, варианты — переводы.
                return item.ToStudyCard(
                    exercise, word.Lemma, PromptLanguageEnglish, word.IpaUk, word.IpaUs, audioUrl, options);
            }

            // Дистракторов не хватило: выбор из одного варианта бессмысленен,
            // поэтому задание становится вводом.
            exercise = ExerciseKind.Spelling;
        }

        // Вопрос — перевод, ответ — английское слово. Самого слова в карточке нет:
        // иначе ответ был бы виден на экране до попытки.
        var translation = WordTranslations.Primary(word);

        var (prompt, promptLanguage) = translation is null
            // Слово без перевода переспросить нечем — остаётся написание по образцу.
            ? (word.Lemma, PromptLanguageEnglish)
            : (translation, PromptLanguageRussian);

        // Подсказка о длине нужна там, где ответ вводят по памяти: на слух
        // «deposit» и «deposit» с лишней буквой неразличимы, а без подсказки
        // диктант превращается в угадывание.
        var answerLength = exercise is ExerciseKind.Spelling or ExerciseKind.Listening
            ? word.Lemma.Length
            : (int?)null;

        // Текст для озвучивания заполняется только у упражнения на слух.
        // Записи в словаре нет, поэтому произносит браузер — и ему нужен текст.
        // Для остальных упражнений это была бы подсказка: клиент произнёс бы
        // ответ вслух до попытки.
        var spokenText = exercise == ExerciseKind.Listening ? word.Lemma : null;

        return item.ToStudyCard(
            exercise, prompt, promptLanguage, word.IpaUk, word.IpaUs, audioUrl,
            options: null, answerLength: answerLength, spokenText: spokenText);
    }

    /// <summary>
    /// Принятые варианты ответа.
    /// </summary>
    /// <remarks>
    /// Принимаются и перевод, и английское слово. Направление задания зависит
    /// от типа упражнения и от того, хватило ли данных для вариантов, поэтому
    /// строгая проверка «только в одну сторону» отмечала бы верный ответ
    /// как ошибку. Для алгоритма повторений важна честность оценки, а не формальность.
    /// </remarks>
    private static List<string> AcceptedAnswers(Word word) =>
        [.. WordTranslations.All(word), word.Lemma];

    private static string? FindAudioUrl(Word word) =>
        word.Examples
            .Select(e => e.AudioUrl)
            .FirstOrDefault(url => !string.IsNullOrWhiteSpace(url));

    /// <summary>
    /// Варианты ответа: правильный перевод плюс дистракторы из той же колоды.
    /// </summary>
    private async Task<IReadOnlyList<string>> BuildOptionsAsync(
        Word word, int wordId, int userId, CancellationToken ct)
    {
        var correct = WordTranslations.Primary(word);

        if (correct is null)
            return [];

        var siblings = await words.GetDistractorsAsync(wordId, ChoiceOptionCount - 1, ct);
        var distractors = siblings
            .SelectMany(w => w.Translations)
            .Select(t => t.Text)
            .Where(t => !string.IsNullOrWhiteSpace(t) && t != correct)
            .Distinct()
            .Take(ChoiceOptionCount - 1)
            .ToList();

        if (distractors.Count < 2)
            return [];

        var options = new List<string> { correct };
        options.AddRange(distractors);

        // Перемешивание детерминировано парой (пользователь, слово): порядок
        // стабилен между запросами, но не совпадает у разных пользователей.
        return options
            .OrderBy(o => unchecked(o.GetHashCode() * 31 ^ userId ^ wordId))
            .ToList();
    }

    private async Task UpdateDailyAdHocAsync(
        User user, DateOnly localDate, bool isCorrect, CancellationToken ct)
    {
        var stat = await dailyStats.GetAsync(user.Id, localDate, ct);
        if (stat is null)
        {
            stat = new DailyStat { UserId = user.Id, Date = localDate };
            await dailyStats.AddAsync(stat, ct);
        }

        stat.ReviewedCount++;
        if (isCorrect)
            stat.CorrectCount++;

        // Цель записывается в тот же день, к которому относится, и обновляется
        // каждым ответом. Сегодняшний день идёт по нынешней цели (её можно
        // поменять и добрать ответы), а прошлые уже зафиксированы и от правки
        // настроек не зависят.
        stat.DailyGoal = (user.Settings ?? new UserSettings()).DailyGoal;
    }
}
