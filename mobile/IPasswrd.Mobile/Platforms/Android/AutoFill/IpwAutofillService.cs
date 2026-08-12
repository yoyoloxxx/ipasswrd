using Android.App;
using Android.Content;
using Android.OS;
using Android.Service.Autofill;
using Android.Views.Autofill;
using Android.Widget;
using IPasswrd.Core;
using IPasswrd.Mobile.Services;

namespace IPasswrd.Mobile.Platforms.Android.AutoFill;

/// <summary>
/// Системное автозаполнение Android — аналог Credential Provider Extension на iOS,
/// только доступный без платного аккаунта разработчика.
///
/// Служба живёт в том же процессе, что и приложение: если сейф уже разблокирован,
/// подсказки с логином и паролем показываются сразу. Если заблокирован — показывается
/// одна строка «IPasswrd», по нажатию открывается экран разблокировки и выбора записи.
/// </summary>
[Service(
    Label = "IPasswrd",
    Permission = "android.permission.BIND_AUTOFILL_SERVICE",
    Exported = true)]
[IntentFilter(new[] { "android.service.autofill.AutofillService" })]
[MetaData("android.autofill", Resource = "@xml/ipw_autofill_service")]
public class IpwAutofillService : AutofillService
{
    private const int MaxInlineDatasets = 6;
    private static int _requestCode = 7100;

    // ================= заполнение =================

    public override void OnFillRequest(FillRequest request, CancellationSignal cancellationSignal, FillCallback callback)
    {
        try
        {
            var contexts = request.FillContexts;
            if (contexts is null || contexts.Count == 0) { callback.OnSuccess(null); return; }

            global::Android.App.Assist.AssistStructure? structure = contexts[contexts.Count - 1].Structure;
            if (structure is null) { callback.OnSuccess(null); return; }

            AutofillFields fields = AutofillParser.Parse(structure);

            // ⚠ ДИАГНОСТИКА (Яндекс): что реально пришло от браузера
            bool compat = ((int)request.Flags & 0x2) != 0;   // FLAG_COMPATIBILITY_MODE_REQUEST
            Console.WriteLine($"[IPW] fill pkg={fields.PackageName} web={fields.WebDomain} compat={compat} " +
                $"user={(fields.Username != null)} pass={(fields.Password != null)} otp={(fields.Otp != null)} " +
                $"nodes={AutofillParser.LastTextNodeCount}");

            if (!fields.HasAny) { callback.OnSuccess(null); return; }

            // Свои же экраны (мастер-пароль!) не заполняем — это было бы абсурдно и небезопасно.
            if (string.Equals(fields.PackageName, PackageName, StringComparison.Ordinal))
            { callback.OnSuccess(null); return; }

            callback.OnSuccess(BuildResponse(fields));
        }
        catch (Exception ex)
        {
            try { callback.OnFailure(ex.Message); } catch (Exception) { }
        }
    }

    private FillResponse BuildResponse(AutofillFields fields)
    {
        var builder = new FillResponse.Builder();
        AutofillId[] ids = fields.Ids();

        Vault? vault = Svc.State.IsUnlocked ? Svc.State.Vault : null;
        int added = 0;

        if (vault is not null)
        {
            bool isBrowser = AutofillMatcher.IsBrowser(fields.PackageName);
            string? web = isBrowser ? fields.WebDomain : null;    // trust a reported web domain only from a real browser
            string? pkg = isBrowser ? null : fields.PackageName;  // otherwise match the native app by its exact package
            foreach (AutofillCandidate c in AutofillMatcher.Matches(vault, web, pkg))
            {
                if (added >= MaxInlineDatasets) break;
                Dataset? ds = BuildDataset(vault, fields, c);
                if (ds is null) continue;
                builder.AddDataset(ds);
                added++;
            }
        }

        // Строка «открыть IPasswrd»: разблокировать сейф либо выбрать другую запись.
        string title = vault is null ? "IPasswrd — разблокировать" : "IPasswrd — другая запись";
        string subtitle = vault is null
            ? "Сейф заблокирован"
            : (added > 0 ? "Показать все записи" : "Подходящих записей нет — открыть список");

        var authDataset = new Dataset.Builder(Presentation(title, subtitle));
        foreach (AutofillId id in ids) authDataset.SetValue(id, null);
        authDataset.SetAuthentication(PickerIntentSender(fields));
        builder.AddDataset(authDataset.Build());

        // Предложение сохранить новый логин/пароль (аналог системного «Сохранить пароль?»).
        var required = new List<AutofillId>();
        if (fields.Password is not null) required.Add(fields.Password);
        if (fields.Username is not null) required.Add(fields.Username);
        if (required.Count > 0)
        {
            SaveDataType type = SaveDataType.Generic;
            if (fields.Password is not null) type |= SaveDataType.Password;
            if (fields.Username is not null) type |= SaveDataType.Username;
            builder.SetSaveInfo(new SaveInfo.Builder(type, required.ToArray()).Build());
        }

        return builder.Build();
    }

    private Dataset? BuildDataset(Vault vault, AutofillFields fields, AutofillCandidate c)
    {
        string login = c.Login;
        string password = c.Password;
        string? code = fields.Otp is not null ? AutofillMatcher.CodeFor(vault, c.Item) : null;

        bool any = (fields.Username is not null && login.Length > 0)
                || (fields.Password is not null && password.Length > 0)
                || (fields.Otp is not null && !string.IsNullOrEmpty(code));
        if (!any) return null;

        string subtitle = login.Length > 0 ? login : "без логина";
        if (fields.Otp is not null && !string.IsNullOrEmpty(code)) subtitle += " · код " + code;

        var ds = new Dataset.Builder(Presentation(c.Title, subtitle));
        // Пустые значения НЕ проставляем: иначе запись без логина стёрла бы то,
        // что человек уже набрал руками. Что хотя бы одно значение непустое — проверено выше.
        if (fields.Username is not null && login.Length > 0)
            ds.SetValue(fields.Username, AutofillValue.ForText(login));
        if (fields.Password is not null && password.Length > 0)
            ds.SetValue(fields.Password, AutofillValue.ForText(password));
        if (fields.Otp is not null && !string.IsNullOrEmpty(code))
            ds.SetValue(fields.Otp, AutofillValue.ForText(code));

        return ds.Build();
    }

    private IntentSender PickerIntentSender(AutofillFields fields)
    {
        var intent = new Intent(this, typeof(AutofillPickerActivity));
        if (fields.Username is not null) intent.PutExtra(AutofillPickerActivity.ExtraUsernameId, fields.Username);
        if (fields.Password is not null) intent.PutExtra(AutofillPickerActivity.ExtraPasswordId, fields.Password);
        if (fields.Otp is not null) intent.PutExtra(AutofillPickerActivity.ExtraOtpId, fields.Otp);
        intent.PutExtra(AutofillPickerActivity.ExtraDomain, fields.WebDomain ?? "");
        intent.PutExtra(AutofillPickerActivity.ExtraPackage, fields.PackageName ?? "");

        int code = Interlocked.Increment(ref _requestCode);
        PendingIntentFlags flags = PendingIntentFlags.CancelCurrent | PendingIntentFlags.Mutable;
        PendingIntent pi = PendingIntent.GetActivity(this, code, intent, flags)!;
        return pi.IntentSender!;
    }

    internal static RemoteViews Presentation(string title, string subtitle)
    {
        var views = new RemoteViews(global::Android.App.Application.Context.PackageName, Resource.Layout.ipw_autofill_item);
        views.SetTextViewText(Resource.Id.ipw_title, title);
        views.SetTextViewText(Resource.Id.ipw_subtitle, subtitle);
        return views;
    }

    // ================= сохранение =================

    public override void OnSaveRequest(SaveRequest request, SaveCallback callback)
    {
        try
        {
            var contexts = request.FillContexts;
            if (contexts is null || contexts.Count == 0) { callback.OnSuccess(); return; }

            global::Android.App.Assist.AssistStructure? structure = contexts[contexts.Count - 1].Structure;
            if (structure is null) { callback.OnSuccess(); return; }

            AutofillFields fields = AutofillParser.Parse(structure);
            if (string.Equals(fields.PackageName, PackageName, StringComparison.Ordinal))
            { callback.OnSuccess(); return; }
            string user = AutofillParser.ValueOf(structure, fields.Username) ?? "";
            string pass = AutofillParser.ValueOf(structure, fields.Password) ?? "";

            if (pass.Length == 0) { callback.OnSuccess(); return; }

            string domain = AutofillMatcher.IsBrowser(fields.PackageName) ? (fields.WebDomain ?? "") : "";
            string pkg = fields.PackageName ?? "";

            if (Svc.State.IsUnlocked)
            {
                _ = Task.Run(async () =>
                {
                    try { await AutofillVaultWriter.SaveAsync(user, pass, domain, pkg); }
                    catch (Exception) { }
                });
            }
            else
            {
                // Сейф закрыт — просим разблокировать отдельным экраном.
                var intent = new Intent(this, typeof(AutofillSaveActivity));
                intent.AddFlags(ActivityFlags.NewTask);
                intent.PutExtra(AutofillSaveActivity.ExtraUsername, user);
                intent.PutExtra(AutofillSaveActivity.ExtraPassword, pass);
                intent.PutExtra(AutofillSaveActivity.ExtraDomain, domain);
                intent.PutExtra(AutofillSaveActivity.ExtraPackage, pkg);
                StartActivity(intent);
            }

            callback.OnSuccess();
        }
        catch (Exception ex)
        {
            try { callback.OnFailure(ex.Message); } catch (Exception) { }
        }
    }
}
