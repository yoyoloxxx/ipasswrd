using System.Collections.Generic;
using System.Text;

namespace IPasswrd.App;

/// <summary>
/// Pure builder for the on-disk SMS-relay helper text (sms-relay.txt).
/// The live bearer must never appear here — it lives only in sms-relay.token (and in memory).
/// Shortcuts should send <c>X-IPW-Token</c> (or append <c>?t=</c> from that file), not copy a URL that embeds the secret.
/// </summary>
internal static class SmsRelayInfoFile
{
    /// <summary>Placeholder shown in example URLs instead of the real token.</summary>
    public const string TokenPlaceholder = "<token from sms-relay.token>";

    public static string Build(string machineName, int port, IEnumerable<string> ipv4Addresses)
    {
        string host = (machineName ?? "").ToLowerInvariant();
        var sb = new StringBuilder();
        sb.AppendLine("IPasswrd — приём СМС-кодов с телефона (для автоматизации «Быстрых команд»).");
        sb.AppendLine();
        sb.AppendLine("В действии «Получить содержимое URL»: метод POST, тело — текст СМС.");
        sb.AppendLine("Телефон должен быть в одной сети с этим компьютером.");
        sb.AppendLine();
        sb.AppendLine("Токен авторизации НЕ хранится в этом файле (чтобы любой процесс пользователя");
        sb.AppendLine("не читал его из plaintext URL). Берите его из рядом лежащего sms-relay.token");
        sb.AppendLine("и передавайте заголовком X-IPW-Token (предпочтительно) либо как ?t=… в адресе.");
        sb.AppendLine();
        sb.AppendLine($"URL по имени компьютера: http://{host}.local:{port}/sms?t={TokenPlaceholder}");
        foreach (string ip in ipv4Addresses)
            sb.AppendLine($"URL по адресу:           http://{ip}:{port}/sms?t={TokenPlaceholder}");
        sb.AppendLine();
        sb.AppendLine("Вне дома (мобильный интернет): вместо URL сохраняйте текст СМС файлом");
        sb.AppendLine("в iCloud Drive → IPasswrd → sms-inbox (действие «Сохранить файл», с заменой).");
        sb.AppendLine();
        sb.AppendLine("БУФЕР ТЕЛЕФОН → ПК, ОСНОВНОЙ ПУТЬ (сеть НЕ нужна):");
        sb.AppendLine("  Быстрая команда «На ПК»: Получить буфер обмена → Показать уведомление");
        sb.AppendLine("  (в теле — переменная «Буфер обмена»). Уведомление едет по Bluetooth");
        sb.AppendLine("  через «Связь с телефоном». Предел канала ~150 символов.");
        sb.AppendLine();
        sb.AppendLine("ДЛИННЫЙ ТЕКСТ (любая сеть с интернетом, задержка 10-60 с):");
        sb.AppendLine("  Действие «Сохранить файл» → iCloud Drive, путь /IPasswrd/clip-inbox/clip.txt");
        sb.AppendLine("  (с заменой, без вопроса куда сохранять). Приложение заберёт и удалит.");
        sb.AppendLine();
        sb.AppendLine("ЗАПАСНОЙ СЕТЕВОЙ ПУТЬ (та же сеть, мгновенно):");
        sb.AppendLine("  Токен — только из sms-relay.token; заголовок X-IPW-Token надёжнее query.");
        sb.AppendLine("  Принимаются только адреса из локальной сети.");
        sb.AppendLine($"  Телефон → ПК: POST http://{host}.local:{port}/clip?t={TokenPlaceholder}   (тело — текст буфера)");
        sb.AppendLine($"  ПК → телефон: GET  http://{host}.local:{port}/clip?t={TokenPlaceholder}   (в ответе JSON, поле text)");
        sb.AppendLine("     ⚠ ПК → телефон (GET /clip) по умолчанию ВЫКЛЮЧЕН — включается в Настройках.");
        return sb.ToString();
    }
}
