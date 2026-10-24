namespace EnglishTrainer.Domain.Enums;

/// <summary>Уровень владения словом по общеевропейской шкале.</summary>
public enum CefrLevel
{
    Unknown = 0,
    A1 = 1,
    A2 = 2,
    B1 = 3,
    B2 = 4,
    C1 = 5,
    C2 = 6
}

/// <summary>
/// Оценка ответа пользователем. Ставится <b>после</b> показа правильного ответа,
/// а не вместо него: иначе пользователь начинает обманывать себя, и алгоритм
/// повторений получает ложные данные.
/// </summary>
public enum ReviewRating
{
    /// <summary>Не вспомнил совсем. Карточка возвращается в оборот в тот же день.</summary>
    Again = 1,

    /// <summary>Вспомнил с трудом.</summary>
    Hard = 2,

    /// <summary>Вспомнил нормально. Основной сценарий.</summary>
    Good = 3,

    /// <summary>Вспомнил мгновенно. Интервал растёт быстрее.</summary>
    Easy = 4
}

/// <summary>Этап жизненного цикла карточки в алгоритме повторений.</summary>
public enum CardPhase
{
    /// <summary>Новое слово, ещё ни разу не показано.</summary>
    New = 0,

    /// <summary>В процессе заучивания: интервалы измеряются минутами и днями.</summary>
    Learning = 1,

    /// <summary>В долгосрочном повторении: интервалы измеряются неделями и месяцами.</summary>
    Review = 2,

    /// <summary>Пользователь забыл слово, интервал сброшен.</summary>
    Relearning = 3
}

/// <summary>Состояние сессии обучения.</summary>
public enum StudySessionStatus
{
    Active = 0,
    Completed = 1,
    /// <summary>Брошена: пользователь ушёл, не пройдя очередь.</summary>
    Abandoned = 2
}

/// <summary>Откуда карточка попала в сессию.</summary>
public enum SessionItemKind
{
    /// <summary>Просроченное повторение.</summary>
    Review = 0,

    /// <summary>Новое слово в рамках дневного лимита.</summary>
    New = 1,

    /// <summary>
    /// Слово из избранного. Такие карточки идут в начале очереди и доступны
    /// отдельной тренировкой, которая не ждёт назначенного срока.
    /// </summary>
    Favorite = 2
}

/// <summary>
/// Срез слов для страниц прогресса: «в работе», «пора повторить», «выучено».
/// </summary>
/// <remarks>
/// Это не этап карточки, а именно срез для списка. «В работе» и «выучено»
/// делят карточки по интервалу, а «пора повторить» — по сроку, поэтому один
/// и тот же слово может попасть и в «в работе», и в «пора повторить».
/// </remarks>
public enum WordProgressScope
{
    /// <summary>Начатые, но ещё не выученные слова.</summary>
    InProgress = 0,

    /// <summary>Слова, срок повторения которых уже наступил.</summary>
    Due = 1,

    /// <summary>Слова с интервалом больше месяца.</summary>
    Learned = 2
}

/// <summary>Состояние заявки в друзья.</summary>
public enum FriendshipStatus
{
    /// <summary>Заявка отправлена и ждёт ответа.</summary>
    Pending = 0,

    /// <summary>Заявка принята, дружба действует.</summary>
    Accepted = 1,

    /// <summary>Заявка отклонена.</summary>
    Declined = 2
}

/// <summary>Как найденный человек связан с текущим пользователем.</summary>
public enum FriendRelation
{
    /// <summary>Никак: можно отправить заявку.</summary>
    None = 0,

    /// <summary>Заявка уже отправлена и ждёт ответа.</summary>
    OutgoingRequest = 1,

    /// <summary>Человек сам отправил заявку — её можно принять.</summary>
    IncomingRequest = 2,

    /// <summary>Уже друзья.</summary>
    Friends = 3
}

/// <summary>Тип упражнения, которым проверяется слово.</summary>
public enum ExerciseKind
{
    /// <summary>Узнавание: выбрать перевод из вариантов.</summary>
    MultipleChoice = 0,

    /// <summary>Воспроизведение: вспомнить и ввести ответ.</summary>
    Typing = 1,

    /// <summary>Аудирование: понять слово на слух.</summary>
    Listening = 2,

    /// <summary>Написание: ввести слово по переводу.</summary>
    Spelling = 3
}

/// <summary>Откуда взялось «слово дня».</summary>
/// <remarks>
/// Различие не косметическое: слово, которое пора повторить, требует действия,
/// а слово из словаря — просто знакомство. Одинаковый текст для обоих случаев
/// превращал бы напоминание в шум.
/// </remarks>
public enum WordOfDaySource
{
    /// <summary>Слово из очереди повторений: срок уже наступил.</summary>
    Review = 0,

    /// <summary>Слово из очереди вперёд: срок ещё не наступил, но ближе всех.</summary>
    Upcoming = 1,

    /// <summary>Слова в изучении нет — показывается слово из словаря.</summary>
    Dictionary = 2
}
