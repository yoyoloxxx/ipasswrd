using System.Text.Json;
using IPasswrd.Core;
using Xunit;

namespace IPasswrd.Core.Tests;

// Форма на Windows собирает запись заново, из полей. Всё, чего в форме нет, при сохранении
// исчезало: скан паспорта, звёздочка, поля от более новой версии. Эти тесты держат границу —
// что форма перезаписывает, а что обязана донести до сейфа нетронутым.
public class FormEditTests
{
    private const string Pw = "correct horse battery staple";
    private static readonly KdfConfig Fast = KdfConfig.Fast;

    private static Attachment Scan(string name) =>
        new() { Name = name, Mime = "image/jpeg", Data = "AAA=", Bytes = 2, AddedAt = "2026-08-07T10:00:00Z" };

    private static VaultItem Saved() => new()
    {
        Type = ItemTypes.Document,
        Title = "Паспорт",
        Favorite = true,
        Attachments = { Scan("скан.jpg") },
        Fields = { ["number"] = "4509 123456" },
    };

    /// <summary>Что приносит форма: тип, название, поля, заметку — и больше ничего.</summary>
    private static VaultItem FromForm(string title) => new()
    {
        Type = ItemTypes.Document,
        Title = title,
        Fields = { ["number"] = "4509 123456" },
    };

    [Fact] // 1
    public void AttachmentsSurviveAnEdit()
    {
        VaultItem next = FormEdit.Carry(Saved(), FromForm("Паспорт РФ"));

        Assert.Single(next.Attachments);
        Assert.Equal("скан.jpg", next.Attachments[0].Name);
    }

    [Fact] // 2
    public void FavoriteSurvivesAnEdit()
    {
        Assert.True(FormEdit.Carry(Saved(), FromForm("Паспорт РФ")).Favorite);
    }

    // Ровно то, ради чего в записи заведён Extra: старая сборка не должна выбрасывать поля,
    // записанные новой. Форма про них не знает по определению — их не было, когда её писали.
    [Fact] // 3
    public void FieldsFromANewerBuildSurviveAnEdit()
    {
        VaultItem saved = Saved();
        saved.Extra = new Dictionary<string, JsonElement>
        {
            ["colour"] = JsonDocument.Parse("\"красный\"").RootElement,
        };

        VaultItem next = FormEdit.Carry(saved, FromForm("Паспорт РФ"));

        Assert.NotNull(next.Extra);
        Assert.Equal("красный", next.Extra!["colour"].GetString());
    }

    [Fact] // 4
    public void WhatTheFormOwnsIsNotTouched()
    {
        VaultItem next = FormEdit.Carry(Saved(), FromForm("Паспорт РФ"));

        Assert.Equal("Паспорт РФ", next.Title);
        Assert.Equal("4509 123456", next.Fields["number"]);
    }

    // Новая запись: переносить не с чего, и падать тоже не с чего.
    [Fact] // 5
    public void NoPreviousRecordIsFine()
    {
        VaultItem next = FormEdit.Carry(null, FromForm("Паспорт"));

        Assert.Empty(next.Attachments);
        Assert.False(next.Favorite);
        Assert.Null(next.Extra);
    }

    // Список копируется, а не одалживается: правка одной записи не должна отзываться в другой.
    [Fact] // 6
    public void TheAttachmentListIsCopiedNotShared()
    {
        VaultItem saved = Saved();
        VaultItem next = FormEdit.Carry(saved, FromForm("Паспорт РФ"));

        next.Attachments.Add(Scan("второй.jpg"));

        Assert.Single(saved.Attachments);
    }

    // Сквозная проверка на настоящем сейфе: правка через форму и обратное чтение.
    [Fact] // 7
    public void EditingThroughTheFormKeepsAttachmentsInTheVault()
    {
        var v = Vault.Create(Pw, Fast);
        string id = v.Add(Saved());

        v.Update(id, FormEdit.Carry(v.Get(id), FromForm("Паспорт РФ")));

        VaultItem back = v.Get(id);
        Assert.Equal("Паспорт РФ", back.Title);
        Assert.Single(back.Attachments);
        Assert.True(back.Favorite);
    }

    // А без переноса — теряется. Тест держит именно эту причину: если однажды Update начнёт
    // достраивать запись сам, здесь станет видно, что FormEdit больше не нужен.
    [Fact] // 8
    public void WithoutCarryTheVaultLosesThem()
    {
        var v = Vault.Create(Pw, Fast);
        string id = v.Add(Saved());

        v.Update(id, FromForm("Паспорт РФ"));

        Assert.Empty(v.Get(id).Attachments);
    }
}
