using EnglishTrainer.Application.Dtos;
using EnglishTrainer.Application.Mapping;
using EnglishTrainer.Application.Text;
using EnglishTrainer.Domain.Abstractions;
using EnglishTrainer.Domain.Entities;
using EnglishTrainer.Domain.Exceptions;
using EnglishTrainer.Domain.Repositories;

namespace EnglishTrainer.Application.Services;

/// <summary>Регистрация, вход и профиль пользователя.</summary>
public sealed class AuthService(
    IUserRepository users,
    IDailyStatRepository dailyStats,
    IUnitOfWork unitOfWork,
    IPasswordHasher passwordHasher,
    ITokenIssuer tokenIssuer,
    IClock clock)
{
    public async Task<AuthResponseDto> RegisterAsync(RegisterRequestDto request, CancellationToken ct = default)
    {
        var email = NormalizeIdentifier(request.Email);
        ValidateEmail(email);

        var password = PasswordPolicy.Require(request.Password);

        if (await users.EmailExistsAsync(email, ct))
            throw new ConflictException("Пользователь с таким email уже зарегистрирован.");

        var user = new User
        {
            Email = email,
            Username = await ReserveUsernameAsync(request.Username, email, ct),
            PasswordHash = passwordHasher.Hash(password),
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName)
                ? email.Split('@')[0]
                : request.DisplayName.Trim(),
            TimeZoneId = NormalizeTimeZone(request.TimeZoneId),
            Settings = new UserSettings()
        };

        await users.AddAsync(user, ct);
        await unitOfWork.SaveChangesAsync(ct);

        return BuildAuthResponse(user);
    }

    /// <summary>
    /// Вход по email или нику.
    /// </summary>
    /// <remarks>
    /// Человек помнит ник, который сам придумал, а email под рукой не всегда —
    /// и наоборот. Поэтому в одном поле принимается и то и другое: сначала поиск
    /// по email, затем по нику. Оба хранятся в нижнем регистре, приведение общее,
    /// так что дополнительной неоднозначности это не создаёт.
    /// </remarks>
    public async Task<AuthResponseDto> LoginAsync(LoginRequestDto request, CancellationToken ct = default)
    {
        var identifier = NormalizeIdentifier(request.Login);

        var user = await users.GetByEmailAsync(identifier, ct)
                   ?? await users.GetByUsernameAsync(identifier, ct);

        // Одна и та же ошибка на «нет пользователя» и «неверный пароль»:
        // иначе можно перебором выяснять, какие адреса зарегистрированы.
        if (user is null || !passwordHasher.Verify(request.Password ?? string.Empty, user.PasswordHash))
            throw new UnauthorizedException("Неверный email, ник или пароль.");

        return BuildAuthResponse(user);
    }

    /// <summary>
    /// Смена пароля изнутри приложения.
    /// </summary>
    /// <remarks>
    /// Текущий пароль обязателен даже при действующем токене: иначе украденный
    /// токен превращается в полный захват аккаунта — достаточно сменить пароль
    /// и не дать владельцу войти.
    /// </remarks>
    public async Task ChangePasswordAsync(
        int userId, ChangePasswordRequestDto request, CancellationToken ct = default)
    {
        var user = await users.GetByIdAsync(userId, ct)
            ?? throw NotFoundException.For("Пользователь", userId);

        if (!passwordHasher.Verify(request.CurrentPassword ?? string.Empty, user.PasswordHash))
            throw new ValidationException("Текущий пароль указан неверно.");

        var newPassword = PasswordPolicy.Require(request.NewPassword);

        // Смысла в смене пароля на тот же самый нет, а ошибка в поле «новый пароль»
        // иначе осталась бы незамеченной: пользователь решил бы, что сменил его.
        if (passwordHasher.Verify(newPassword, user.PasswordHash))
            throw new ValidationException("Новый пароль совпадает с текущим.");

        user.PasswordHash = passwordHasher.Hash(newPassword);

        await unitOfWork.SaveChangesAsync(ct);
    }

    public async Task<UserSettingsDto> UpdateSettingsAsync(
        int userId, UpdateSettingsRequestDto request, CancellationToken ct = default)
    {
        var user = await users.GetWithSettingsAsync(userId, ct)
            ?? throw NotFoundException.For("Пользователь", userId);

        var settings = user.Settings ??= new UserSettings();

        if (request.NewWordsPerDay is { } newPerDay)
        {
            // Границы не косметические: 200 новых слов в день гарантированно
            // приводят к «стене» повторений и уходу пользователя на третьи сутки.
            if (newPerDay is < 0 or > 100)
                throw new ValidationException("Количество новых слов в день должно быть от 0 до 100.");

            settings.NewWordsPerDay = newPerDay;
        }

        if (request.MaxCardsPerSession is { } maxCards)
        {
            if (maxCards is < 5 or > 500)
                throw new ValidationException("Размер сессии должен быть от 5 до 500 карточек.");

            settings.MaxCardsPerSession = maxCards;
        }

        if (request.DailyGoal is { } goal)
        {
            if (goal is < 1 or > 1000)
                throw new ValidationException("Дневная цель должна быть от 1 до 1000 ответов.");

            settings.DailyGoal = goal;

            // Сегодняшний день ещё не закончился, и новая цель применяется к нему
            // сразу: «сделал двадцать ответов при цели тридцать, снизил цель
            // до двадцати — серия засчиталась» должно работать без ещё одного
            // ответа. Прошлые дни при этом не трогаются: они уже зафиксированы
            // в своих записях, и поднятая сегодня цель их не переписывает.
            await ApplyGoalToTodayAsync(user, ct);
        }

        if (request.DailyReminderEnabled is { } reminders)
            settings.DailyReminderEnabled = reminders;

        if (request.ReminderTime is { } reminderTime)
            settings.ReminderTime = reminderTime;

        if (request.ShowTranscriptionUpfront is { } transcription)
            settings.ShowTranscriptionUpfront = transcription;

        // Расписание напоминаний пересчитывается после любой правки настроек,
        // а не только когда менялось время: включение и выключение тоже меняют
        // расписание, а часовой пояс и признак привязки Telegram берутся
        // из самого пользователя.
        user.ScheduleReminder(clock.UtcNow);

        await unitOfWork.SaveChangesAsync(ct);
        return settings.ToDto();
    }

    public async Task UpdateTimeZoneAsync(int userId, string timeZoneId, CancellationToken ct = default)
    {
        var user = await users.GetWithSettingsAsync(userId, ct)
            ?? throw NotFoundException.For("Пользователь", userId);

        user.TimeZoneId = NormalizeTimeZone(timeZoneId);

        // Смена пояса переносит и напоминание: «19:00» — это 19:00 пользователя,
        // и после переезда оно обязано означать новое местное время.
        user.ScheduleReminder(clock.UtcNow);

        await unitOfWork.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Переносит новую дневную цель в запись сегодняшнего дня.
    /// </summary>
    /// <remarks>
    /// Цель хранится в дневной статистике, а не берётся из настроек при подсчёте
    /// серии: иначе повышение плана задним числом рвало бы уже набранную серию.
    /// За сегодняшний день беспокоиться не о чем — он ещё идёт, и человек,
    /// поменявший цель, ждёт, что она подействует сейчас.
    /// </remarks>
    private async Task ApplyGoalToTodayAsync(User user, CancellationToken ct)
    {
        var today = clock.TodayFor(user.TimeZoneId);
        var stat = await dailyStats.GetAsync(user.Id, today, ct);

        if (stat is null)
            return;

        stat.DailyGoal = (user.Settings ?? new UserSettings()).DailyGoal;
    }

    /// <summary>
    /// Определяет ник нового пользователя.
    /// </summary>
    /// <remarks>
    /// Если ник выбран человеком — он либо свободен, либо регистрация отклоняется.
    /// Молча подставить «похожий» нельзя: пользователь вводил конкретный ник
    /// и будет искать себя по нему. А вот ник, выведенный из email, ничей не выбор,
    /// поэтому к нему допустимо добавить числовой хвост.
    /// </remarks>
    private async Task<string> ReserveUsernameAsync(string? requested, string email, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            var chosen = UsernamePolicy.Require(requested);

            if (await users.UsernameExistsAsync(chosen, ct))
                throw new ConflictException($"Ник «{chosen}» уже занят.");

            return chosen;
        }

        var suggested = UsernamePolicy.SuggestFromEmail(email);

        if (!await users.UsernameExistsAsync(suggested, ct))
            return suggested;

        for (var suffix = 2; suffix <= 99; suffix++)
        {
            var candidate = UsernamePolicy.WithSuffix(suggested, suffix);

            if (!await users.UsernameExistsAsync(candidate, ct))
                return candidate;
        }

        return UsernamePolicy.WithSuffix(suggested, Random.Shared.Next(100, 9999));
    }

    private AuthResponseDto BuildAuthResponse(User user)
    {
        var token = tokenIssuer.Issue(user.Id, user.PublicId, user.Email);
        return new AuthResponseDto(token.Value, token.ExpiresAtUtc, user.ToDto());
    }

    /// <summary>
    /// Приводит логин к каноническому виду.
    /// </summary>
    /// <remarks>
    /// Работает и для email, и для ника: оба хранятся в нижнем регистре, поэтому
    /// «Ivan@Mail.ru» и «ivan@mail.ru», как и «Ivan» и «ivan», — один и тот же вход.
    /// Ведущая «@» отбрасывается: ник в профиле показывается как «@ivan», и люди
    /// часто переносят его в поле входа вместе с собачкой. Для email это ничего
    /// не меняет — корректный адрес с «@» не начинается.
    /// </remarks>
    public static string NormalizeIdentifier(string? value) =>
        (value ?? string.Empty).Trim().TrimStart('@').ToLowerInvariant();

    private static void ValidateEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@') || email.StartsWith('@') || email.EndsWith('@'))
            throw new ValidationException("Некорректный email.");
    }

    private static string NormalizeTimeZone(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
            return "UTC";

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId).Id;
        }
        catch (TimeZoneNotFoundException)
        {
            throw new ValidationException($"Неизвестный часовой пояс: '{timeZoneId}'.");
        }
        catch (InvalidTimeZoneException)
        {
            throw new ValidationException($"Некорректный часовой пояс: '{timeZoneId}'.");
        }
    }
}
