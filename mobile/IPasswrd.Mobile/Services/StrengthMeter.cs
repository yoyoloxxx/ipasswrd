using IPasswrd.Core;

namespace IPasswrd.Mobile.Services;

/// <summary>
/// Индикатор стойкости мастер-пароля — та же шкала, что на ПК (SecurityAudit.Rate):
/// длина и число классов символов. Не оценка «на глаз», а то самое правило, по которому
/// аудит потом пометил бы пароль слабым.
/// </summary>
public static class StrengthMeter
{
    public static void Show(Label label, string? password)
    {
        string pw = password ?? "";
        if (pw.Length == 0) { label.IsVisible = false; return; }
        (string text, string hex) = SecurityAudit.Rate(pw) switch
        {
            Strength.Weak => ("Стойкость: слабый — такой подбирают быстро", "#E4574F"),
            Strength.Fair => ("Стойкость: средний — лучше длиннее и разнообразнее", "#E1B85E"),
            _             => ("Стойкость: надёжный", "#58B368"),
        };
        label.Text = text;
        label.TextColor = Color.FromArgb(hex);
        label.IsVisible = true;
    }
}
