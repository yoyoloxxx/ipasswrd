using System.Text;
using IPasswrd.Core;

namespace IPasswrd.Mobile.Services;

/// <summary>
/// Выгрузка сейфа в CSV для переезда в другой менеджер паролей. Формат — как у Chrome/
/// большинства менеджеров: name,url,username,password,note,totp. Открытый текст, поэтому
/// вызывается только после явного предупреждения; файл живёт лишь на время «Поделиться».
/// </summary>
public static class Exporter
{
    public static string ToCsv(IEnumerable<VaultEntry> items)
    {
        var sb = new StringBuilder();
        sb.Append("name,url,username,password,note,totp\r\n");

        foreach (VaultEntry e in items)
        {
            VaultItem it = e.Item;
            if (it.Type is not ("account" or "passkey")) continue;   // логины; карты/доки/заметки не в CSV-формате менеджеров

            string name = it.Title;
            string url = it.Fields.GetValueOrDefault("url", "");
            string user = it.Fields.GetValueOrDefault("username", "");
            string pass = it.Fields.GetValueOrDefault("password", "");
            string note = it.Notes ?? "";
            string totp = it.Fields.GetValueOrDefault("totp", "");

            sb.Append(Field(name)).Append(',')
              .Append(Field(url)).Append(',')
              .Append(Field(user)).Append(',')
              .Append(Field(pass)).Append(',')
              .Append(Field(note)).Append(',')
              .Append(Field(totp)).Append("\r\n");
        }
        return sb.ToString();
    }

    /// <summary>Экранирование по RFC 4180: оборачиваем в кавычки, если есть запятая, кавычка
    /// или перевод строки; внутренние кавычки удваиваются.</summary>
    private static string Field(string value)
    {
        value ??= "";
        bool needQuotes = value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0;
        if (!needQuotes) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
