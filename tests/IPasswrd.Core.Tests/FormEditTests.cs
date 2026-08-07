using System.Text.Json;
using IPasswrd.Core;
using Xunit;

namespace IPasswrd.Core.Tests;

// Форма на Windows собирает запись заново, из полей. Всё, чего в форме нет, при сохранении
// исчезало: звёздочка, поля от более новой версии, скан паспорта. Эти тесты держат границу —
// что форма перезаписывает сама (поля, заметку, вложения — у неё теперь свой список), а что
// обязана донести до сейфа нетронутым (звёздочку и чужие поля).
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

    /// <summary>Что приносит форма: тип, название, поля, СВОЙ список вложений — и больше ничего.</summary>
    private static VaultItem FromForm(string title, params Attachment[] atts)
    {
        var it = new VaultItem { Type = ItemTypes.Document, Title = title, Fields = { ["number"] = "4509 123456" } };
        it.Attachments.AddRange(atts);
        return it;
    }

    [Fact] // 1
    public void FavoriteSurvivesAnEdit()
    {
        Assert.True(FormEdit.Carry(Saved(), FromForm("Паспорт РФ")).Favorite);
    }

    // Ровно то, ради чего в записи заведён Extra: старая сборка не должна выбрасывать поля,
    // записанные новой. Форма про них не знает по определению — их не было, когда её писали.
    [Fact] // 2
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

    [Fact] // 3
    public void WhatTheFormOwnsIsNotTouched()
    {
        VaultItem next = FormEdit.Carry(Saved(), FromForm("Паспорт РФ"));

        Assert.Equal("Паспорт РФ", next.Title);
        Assert.Equal("4509 123456", next.Fields["number"]);
    }

    // Вложения — забота формы: у неё свой список, и Carry в него не лезет. Иначе удалённое
    // последним вложение воскресало бы при каждом сохранении.
    [Fact] // 4
    public void RemovingTheLastAttachmentSticks()
    {
        VaultItem next = FormEdit.Carry(Saved(), FromForm("Паспорт РФ" /* без вложений */));

        Assert.Empty(next.Attachments);
    }

    [Fact] // 5
    public void AttachmentsBroughtByTheFormPassThrough()
    {
        VaultItem next = FormEdit.Carry(Saved(), FromForm("Паспорт РФ", Scan("новый.jpg")));

        Assert.Single(next.Attachments);
        Assert.Equal("новый.jpg", next.Attachments[0].Name);
    }

    // Новая запись: переносить не с чего, и падать тоже не с чего.
    [Fact] // 6
    public void NoPreviousRecordIsFine()
    {
        VaultItem next = FormEdit.Carry(null, FromForm("Паспорт"));

        Assert.Empty(next.Attachments);
        Assert.False(next.Favorite);
        Assert.Null(next.Extra);
    }

    // Сквозная проверка на настоящем сейфе — путь редактора: открыть запись, скопировать её
    // вложения в форму (так делает OpenEditor), собрать заново, сохранить, прочитать обратно.
    [Fact] // 7
    public void EditingThroughTheFormKeepsEverythingInTheVault()
    {
        var v = Vault.Create(Pw, Fast);
        string id = v.Add(Saved());

        VaultItem opened = v.Get(id);
        VaultItem built = FromForm("Паспорт РФ", opened.Attachments.ToArray());   // форма показала прежние файлы
        v.Update(id, FormEdit.Carry(opened, built));

        VaultItem back = v.Get(id);
        Assert.Equal("Паспорт РФ", back.Title);
        Assert.Single(back.Attachments);
        Assert.Equal("скан.jpg", back.Attachments[0].Name);
        Assert.True(back.Favorite);
    }

    // А без переноса звёздочка теряется. Тест держит причину рядом с лекарством: если однажды
    // Update начнёт достраивать запись сам, здесь станет видно, что FormEdit больше не нужен.
    [Fact] // 8
    public void WithoutCarryTheVaultLosesTheStar()
    {
        var v = Vault.Create(Pw, Fast);
        string id = v.Add(Saved());

        v.Update(id, FromForm("Паспорт РФ"));

        Assert.False(v.Get(id).Favorite);
    }
}
