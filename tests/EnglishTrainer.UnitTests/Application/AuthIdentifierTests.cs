using EnglishTrainer.Application.Services;

namespace EnglishTrainer.UnitTests.Application;

/// <summary>
/// Нормализация логина: email и ник должны приводиться к одному виду.
/// </summary>
/// <remarks>
/// Ведущая «@» — не косметика. В профиле ник показывается как «@ivan», и его
/// копируют в поле входа целиком; если сервер не снимает собачку, человек
/// получает «неверный email, ник или пароль» при правильном пароле. Тест
/// фиксирует это поведение рядом с остальными правилами идентификаторов.
/// </remarks>
public sealed class AuthIdentifierTests
{
    [Theory]
    [InlineData("@nail", "nail")]
    [InlineData("  @Nail  ", "nail")]
    [InlineData("nail", "nail")]
    [InlineData("Nail@Example.com", "nail@example.com")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void NormalizeIdentifier_StripsLeadingAtAndNormalizesCase(string? input, string expected)
    {
        Assert.Equal(expected, AuthService.NormalizeIdentifier(input));
    }
}
