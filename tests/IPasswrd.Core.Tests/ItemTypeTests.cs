using IPasswrd.Core;
using Xunit;

namespace IPasswrd.Core.Tests;

// Две сборки успели назвать один и тот же тип по-разному, и запись, заведённая на телефоне,
// пропадала из раздела на компьютере. Эти тесты держат договор: в сейфе живёт одно написание,
// а чужое — приводится к нему при чтении, включая записи, лежащие там с прошлых версий.
public class ItemTypeTests
{
    private const string Pw = "correct horse battery staple";
    private static readonly KdfConfig Fast = KdfConfig.Fast;

    [Fact] // 1
    public void PhoneSpellingBecomesTheCanonicalOne()
    {
        var v = Vault.Create(Pw, Fast);
        string id = v.Add(new VaultItem { Type = "document", Title = "Паспорт" });

        Assert.Equal(ItemTypes.Document, v.Get(id).Type);
    }

    [Fact] // 2
    public void RecordWrittenByTheOtherSpellingIsVisibleAfterReopen()
    {
        var v = Vault.Create(Pw, Fast);
        v.Add(new VaultItem { Type = "document", Title = "Паспорт" });

        var reopened = Vault.Unlock(v.Serialize(), Pw);

        // Именно этот запрос делает раздел «Документы» — он и не находил запись с телефона.
        Assert.Single(reopened.Items(), x => x.Item.Type == ItemTypes.Document);
    }

    [Fact] // 3
    public void UnknownTypeIsLeftAlone()
    {
        // Тип из более новой сборки должен доехать назад неиспорченным — как и незнакомые поля.
        var v = Vault.Create(Pw, Fast);
        string id = v.Add(new VaultItem { Type = "something-new", Title = "?" });

        Assert.Equal("something-new", Vault.Unlock(v.Serialize(), Pw).Get(id).Type);
    }

    [Fact] // 4
    public void EmptyTypeReadsAsAnAccount()
    {
        Assert.Equal(ItemTypes.Account, ItemTypes.Normalize(null));
        Assert.Equal(ItemTypes.Account, ItemTypes.Normalize(""));
    }

    [Fact] // 5
    public void IdentityAliasesLandOnOneName()
    {
        Assert.Equal(ItemTypes.Identity, ItemTypes.Normalize("address"));
        Assert.Equal(ItemTypes.Identity, ItemTypes.Normalize("identities"));
        Assert.Equal(ItemTypes.Identity, ItemTypes.Normalize(ItemTypes.Identity));
    }
}
