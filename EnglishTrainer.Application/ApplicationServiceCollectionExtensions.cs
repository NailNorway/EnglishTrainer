using EnglishTrainer.Application.Services;
using EnglishTrainer.Domain.Abstractions;
using EnglishTrainer.Domain.Scheduling;
using Microsoft.Extensions.DependencyInjection;

namespace EnglishTrainer.Application;

/// <summary>
/// Регистрация сервисов слоя Application.
/// </summary>
/// <remarks>
/// Слой знает только о своих портах (интерфейсах). Реализации — часов, хешера,
/// репозиториев — подставляет Infrastructure. Благодаря этому Application
/// не зависит от EF Core и его можно тестировать без базы.
/// </remarks>
public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<AuthService>();
        services.AddScoped<AchievementService>();
        services.AddScoped<CustomDeckService>();
        services.AddScoped<DeckService>();
        services.AddScoped<FavoriteService>();
        services.AddScoped<FriendService>();
        services.AddScoped<LeaderboardService>();
        services.AddScoped<PasswordResetService>();
        services.AddScoped<ProfileService>();
        // Публичный каталог обслуживает страницы без входа — их читают
        // поисковые роботы, и им же отвечает sitemap.xml.
        services.AddScoped<PublicCatalogService>();
        services.AddScoped<StudySessionService>();
        services.AddScoped<StatsService>();
        services.AddScoped<TelegramService>();
        services.AddScoped<TelegramReminderService>();

        services.AddSingleton<ISrsScheduler>(_ => new Sm2SrsScheduler());
        services.AddSingleton<SrsOptions>();

        return services;
    }
}
