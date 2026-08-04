using AuthenticationServices;
using Foundation;
using IPasswrd.Core;
using ObjCRuntime;
using UIKit;

namespace IPasswrd.AutoFill;

/// <summary>Системное автозаполнение iOS: появляется в Настройки → Автозаполнение и пароли.
/// Показывает аккаунты сейфа (подходящие сайту — сверху), по тапу подставляет логин и пароль;
/// если у записи есть код проверки — он копируется в буфер обмена.</summary>
[Register("CredentialProviderViewController")]
public class CredentialProviderViewController : ASCredentialProviderViewController
{
    private sealed class Row
    {
        public string Id = "";
        public string User = "";
        public string Password = "";
        public string Site = "";
        public string Totp = "";
        public bool Match;
    }

    private ASCredentialServiceIdentifier[] _services = Array.Empty<ASCredentialServiceIdentifier>();
    private string? _targetRecordId;
    private Vault? _vault;
    private readonly List<Row> _rows = new();
    private UITableView? _table;
    private UILabel? _status;

    public CredentialProviderViewController(NativeHandle handle) : base(handle) { }

    // ================= входные точки расширения =================

    public override void PrepareCredentialList(ASCredentialServiceIdentifier[] serviceIdentifiers)
    {
        _services = serviceIdentifiers ?? Array.Empty<ASCredentialServiceIdentifier>();
        OpenVaultThenShow();
    }

    public override void PrepareInterfaceToProvideCredential(ASPasswordCredentialIdentity credentialIdentity)
    {
        _targetRecordId = credentialIdentity.RecordIdentifier;
        _services = credentialIdentity.ServiceIdentifier is null
            ? Array.Empty<ASCredentialServiceIdentifier>()
            : new[] { credentialIdentity.ServiceIdentifier };
        OpenVaultThenShow();
    }

    /// <summary>Тихий путь для подсказки над клавиатурой: без UI. Получается, только если
    /// сессионный ключ ещё в Keychain; иначе iOS сам откроет наш интерфейс.</summary>
    public override void ProvideCredentialWithoutUserInteraction(ASPasswordCredentialIdentity credentialIdentity)
    {
        try
        {
            byte[]? blob = AppGroup.ReadVault();
            byte[]? dek = AppGroup.LoadDek();
            if (blob is null || dek is null) { CancelWith(ASExtensionErrorCode.UserInteractionRequired); return; }

            Vault v = Vault.UnlockWithSessionKey(blob, dek);
            string? id = credentialIdentity.RecordIdentifier;
            VaultItem? it = null;
            if (!string.IsNullOrEmpty(id))
            {
                try { it = v.Get(id!); } catch { it = null; }
            }
            if (it is null) { CancelWith(ASExtensionErrorCode.CredentialIdentityNotFound); return; }

            CopyTotpIfAny(it);
            ExtensionContext!.CompleteRequest(
                new ASPasswordCredential(it.Fields.GetValueOrDefault("username", ""),
                                         it.Fields.GetValueOrDefault("password", "")), null);
        }
        catch
        {
            CancelWith(ASExtensionErrorCode.UserInteractionRequired);
        }
    }

    /// <summary>Экран после включения IPasswrd в Настройках.</summary>
    public override void PrepareInterfaceForExtensionConfiguration()
    {
        View!.BackgroundColor = UIColor.SystemBackground;
        var label = new UILabel
        {
            Text = "Автозаполнение IPasswrd включено.\n\nОткройте IPasswrd и разблокируйте сейф — логины появятся в подсказках над клавиатурой.",
            Lines = 0,
            TextAlignment = UITextAlignment.Center,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        var done = UIButton.FromType(UIButtonType.System);
        done.SetTitle("Готово", UIControlState.Normal);
        done.TitleLabel!.Font = UIFont.BoldSystemFontOfSize(17);
        done.TranslatesAutoresizingMaskIntoConstraints = false;
        done.TouchUpInside += (_, _) => ExtensionContext!.CompleteExtensionConfigurationRequest();

        View.AddSubview(label);
        View.AddSubview(done);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            label.CenterYAnchor.ConstraintEqualTo(View.CenterYAnchor, -40),
            label.LeadingAnchor.ConstraintEqualTo(View.LeadingAnchor, 32),
            label.TrailingAnchor.ConstraintEqualTo(View.TrailingAnchor, -32),
            done.TopAnchor.ConstraintEqualTo(label.BottomAnchor, 28),
            done.CenterXAnchor.ConstraintEqualTo(View.CenterXAnchor),
        });
    }

    // ================= открытие сейфа =================

    private void OpenVaultThenShow()
    {
        BuildChrome();

        byte[]? blob = AppGroup.ReadVault();
        if (blob is null)
        {
            ShowStatus("Сейф ещё не передан расширению.\nОткройте IPasswrd, разблокируйте сейф и попробуйте снова.");
            return;
        }

        byte[]? dek = AppGroup.LoadDek();
        if (dek is not null)
        {
            ShowStatus("Открываю сейф…");
            Task.Run(() =>
            {
                try
                {
                    Vault v = Vault.UnlockWithSessionKey(blob, dek);
                    InvokeOnMainThread(() => { _vault = v; FillRows(); });
                }
                catch
                {
                    AppGroup.WipeDek();
                    InvokeOnMainThread(AskPassword);
                }
            });
            return;
        }

        AskPassword();
    }

    private void AskPassword()
    {
        ShowStatus("Нужен мастер-пароль.");
        var alert = UIAlertController.Create("IPasswrd", "Введите мастер-пароль сейфа", UIAlertControllerStyle.Alert);
        alert.AddTextField(tf =>
        {
            tf.SecureTextEntry = true;
            tf.Placeholder = "Мастер-пароль";
        });
        alert.AddAction(UIAlertAction.Create("Отмена", UIAlertActionStyle.Cancel,
            _ => CancelWith(ASExtensionErrorCode.UserCanceled)));
        alert.AddAction(UIAlertAction.Create("Открыть", UIAlertActionStyle.Default, _ =>
        {
            string pw = alert.TextFields![0].Text ?? "";
            ShowStatus("Открываю сейф…\n(первый раз может занять несколько секунд)");
            byte[]? blob = AppGroup.ReadVault();
            if (blob is null || pw.Length == 0) { AskPassword(); return; }
            Task.Run(() =>
            {
                try
                {
                    Vault v = Vault.Unlock(blob, pw);
                    AppGroup.SaveDek(v.ExportSessionKey());
                    InvokeOnMainThread(() => { _vault = v; FillRows(); });
                }
                catch (WrongMasterPasswordException)
                {
                    InvokeOnMainThread(() => { ShowStatus("Неверный мастер-пароль."); AskPassword(); });
                }
                catch
                {
                    InvokeOnMainThread(() => ShowStatus("Не удалось открыть сейф."));
                }
            });
        }));
        PresentViewController(alert, true, null);
    }

    // ================= данные и таблица =================

    private static readonly HashSet<string> FillTypes = new() { "account" };

    private void FillRows()
    {
        if (_vault is null) return;
        _rows.Clear();

        var domains = new List<string>();
        foreach (ASCredentialServiceIdentifier s in _services)
        {
            string ident = s.Identifier ?? "";
            if (s.Type == ASCredentialServiceIdentifierType.Url && Uri.TryCreate(ident, UriKind.Absolute, out var u))
                ident = u.Host;
            string dom = Dedup.RegistrableDomain(ident);
            if (dom.Length == 0) dom = ident.Trim().ToLowerInvariant();
            if (dom.Length > 0) domains.Add(dom);
        }

        try
        {
            foreach (VaultEntry e in _vault.Items())
            {
                if (!FillTypes.Contains(e.Item.Type)) continue;
                string user = e.Item.Fields.GetValueOrDefault("username", "");
                string pass = e.Item.Fields.GetValueOrDefault("password", "");
                if (user.Length == 0 && pass.Length == 0) continue;

                string url = e.Item.Fields.GetValueOrDefault("url", "");
                string dom = Dedup.RegistrableDomain(url);
                string site = dom.Length > 0 ? dom : (e.Item.Title.Length > 0 ? e.Item.Title : url);
                bool match = dom.Length > 0 && domains.Contains(dom);

                _rows.Add(new Row
                {
                    Id = e.Id,
                    User = user.Length > 0 ? user : "(без логина)",
                    Password = pass,
                    Site = site,
                    Totp = e.Item.Fields.GetValueOrDefault("totp", ""),
                    Match = match,
                });
            }
        }
        catch { }

        _rows.Sort((a, b) =>
        {
            int m = b.Match.CompareTo(a.Match);
            if (m != 0) return m;
            int s = string.Compare(a.Site, b.Site, StringComparison.CurrentCultureIgnoreCase);
            return s != 0 ? s : string.Compare(a.User, b.User, StringComparison.CurrentCultureIgnoreCase);
        });

        // Прямой переход от подсказки: если запись известна — заполняем сразу.
        if (_targetRecordId is not null)
        {
            Row? t = _rows.FirstOrDefault(r => r.Id == _targetRecordId);
            if (t is not null) { Complete(t); return; }
        }

        _status?.RemoveFromSuperview();
        _status = null;
        _table!.ReloadData();
    }

    private void Complete(Row r)
    {
        if (_vault is not null)
        {
            try { CopyTotpIfAny(_vault.Get(r.Id)); } catch { }
        }
        ExtensionContext!.CompleteRequest(new ASPasswordCredential(r.User == "(без логина)" ? "" : r.User, r.Password), null);
    }

    private static void CopyTotpIfAny(VaultItem it)
    {
        string totp = it.Fields.GetValueOrDefault("totp", "");
        if (totp.Length == 0) return;
        try
        {
            TotpConfig cfg = Totp.Parse(totp);
            UIPasteboard.General.String = Totp.Generate(cfg.Secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), cfg.Digits, cfg.Period, cfg.Algorithm);
        }
        catch { }
    }

    private void CancelWith(ASExtensionErrorCode code) =>
        ExtensionContext!.CancelRequest(new NSError(new NSString("ASExtensionErrorDomain"), (nint)(long)code));

    // ================= интерфейс =================

    private void BuildChrome()
    {
        if (_table is not null) return;
        View!.BackgroundColor = UIColor.SystemBackground;

        var bar = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
        var title = new UILabel
        {
            Text = "IPasswrd",
            Font = UIFont.BoldSystemFontOfSize(17),
            TextAlignment = UITextAlignment.Center,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        var cancel = UIButton.FromType(UIButtonType.System);
        cancel.SetTitle("Отмена", UIControlState.Normal);
        cancel.TranslatesAutoresizingMaskIntoConstraints = false;
        cancel.TouchUpInside += (_, _) => CancelWith(ASExtensionErrorCode.UserCanceled);

        bar.AddSubview(title);
        bar.AddSubview(cancel);
        View.AddSubview(bar);

        _table = new UITableView(CoreGraphics.CGRect.Empty, UITableViewStyle.InsetGrouped)
        {
            TranslatesAutoresizingMaskIntoConstraints = false,
            Source = new Source(this),
        };
        View.AddSubview(_table);

        NSLayoutConstraint.ActivateConstraints(new[]
        {
            bar.TopAnchor.ConstraintEqualTo(View.SafeAreaLayoutGuide.TopAnchor),
            bar.LeadingAnchor.ConstraintEqualTo(View.LeadingAnchor),
            bar.TrailingAnchor.ConstraintEqualTo(View.TrailingAnchor),
            bar.HeightAnchor.ConstraintEqualTo(48),
            title.CenterXAnchor.ConstraintEqualTo(bar.CenterXAnchor),
            title.CenterYAnchor.ConstraintEqualTo(bar.CenterYAnchor),
            cancel.LeadingAnchor.ConstraintEqualTo(bar.LeadingAnchor, 16),
            cancel.CenterYAnchor.ConstraintEqualTo(bar.CenterYAnchor),
            _table.TopAnchor.ConstraintEqualTo(bar.BottomAnchor),
            _table.LeadingAnchor.ConstraintEqualTo(View.LeadingAnchor),
            _table.TrailingAnchor.ConstraintEqualTo(View.TrailingAnchor),
            _table.BottomAnchor.ConstraintEqualTo(View.BottomAnchor),
        });
    }

    private void ShowStatus(string text)
    {
        if (_status is null)
        {
            _status = new UILabel
            {
                Lines = 0,
                TextAlignment = UITextAlignment.Center,
                TextColor = UIColor.SecondaryLabel,
                Font = UIFont.SystemFontOfSize(15),
                TranslatesAutoresizingMaskIntoConstraints = false,
            };
            View!.AddSubview(_status);
            NSLayoutConstraint.ActivateConstraints(new[]
            {
                _status.CenterYAnchor.ConstraintEqualTo(View.CenterYAnchor),
                _status.LeadingAnchor.ConstraintEqualTo(View.LeadingAnchor, 32),
                _status.TrailingAnchor.ConstraintEqualTo(View.TrailingAnchor, -32),
            });
        }
        _status.Text = text;
        View!.BringSubviewToFront(_status);
    }

    private sealed class Source : UITableViewSource
    {
        private readonly CredentialProviderViewController _c;
        public Source(CredentialProviderViewController c) => _c = c;

        public override nint NumberOfSections(UITableView tableView)
        {
            bool hasMatch = _c._rows.Any(r => r.Match);
            bool hasOther = _c._rows.Any(r => !r.Match);
            return hasMatch && hasOther ? 2 : 1;
        }

        private List<Row> RowsFor(nint section)
        {
            bool hasMatch = _c._rows.Any(r => r.Match);
            bool matchSection = hasMatch && section == 0;
            return _c._rows.Where(r => r.Match == matchSection || !hasMatch).ToList();
        }

        public override nint RowsInSection(UITableView tableview, nint section) => RowsFor(section).Count;

        public override string TitleForHeader(UITableView tableView, nint section)
        {
            bool hasMatch = _c._rows.Any(r => r.Match);
            if (!hasMatch) return "Все аккаунты";
            return section == 0 ? "Для этого сайта" : "Все аккаунты";
        }

        public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
        {
            UITableViewCell cell = tableView.DequeueReusableCell("c") ?? new UITableViewCell(UITableViewCellStyle.Subtitle, "c");
            Row r = RowsFor(indexPath.Section)[indexPath.Row];
            cell.TextLabel!.Text = r.User;
            if (cell.DetailTextLabel is not null)
            {
                cell.DetailTextLabel.Text = r.Site + (r.Totp.Length > 0 ? "  ·  код скопируется" : "");
                cell.DetailTextLabel.TextColor = UIColor.SecondaryLabel;
            }
            return cell;
        }

        public override void RowSelected(UITableView tableView, NSIndexPath indexPath)
        {
            tableView.DeselectRow(indexPath, true);
            _c.Complete(RowsFor(indexPath.Section)[indexPath.Row]);
        }
    }
}
