using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IPasswrd.Core;
using Xunit;

namespace IPasswrd.Core.Tests;

// Attachments ride inside the record's own ciphertext. These tests pin the two things that
// could go wrong quietly: a scan leaking into the clear, and a scan disappearing on save.
public class AttachmentTests
{
    private const string Pw = "correct horse battery staple";
    private static readonly KdfConfig Fast = KdfConfig.Fast;

    private static Attachment Scan(string name = "passport.jpg", int bytes = 4096)
    {
        var payload = new byte[bytes];
        for (int i = 0; i < bytes; i++) payload[i] = (byte)(i % 251);
        return new Attachment
        {
            Name = name, Mime = "image/jpeg", Bytes = bytes,
            Data = Convert.ToBase64String(payload),
            AddedAt = "2026-08-06T21:00:00Z",
        };
    }

    private static VaultItem Doc(params Attachment[] files)
    {
        var item = new VaultItem { Type = "document", Title = "Паспорт" };
        item.Attachments.AddRange(files);
        return item;
    }

    [Fact] // 1
    public void AttachmentSurvivesSaveAndReopen()
    {
        var v = Vault.Create(Pw, Fast);
        string id = v.Add(Doc(Scan()));

        var reopened = Vault.Unlock(v.Serialize(), Pw);
        Attachment got = Assert.Single(reopened.Get(id).Attachments);

        Assert.Equal("passport.jpg", got.Name);
        Assert.Equal(4096, Convert.FromBase64String(got.Data).Length);
    }

    [Fact] // 2
    public void AttachmentIsNotStoredInTheClear()
    {
        var v = Vault.Create(Pw, Fast);
        var scan = Scan();
        v.Add(Doc(scan));

        string blob = Encoding.UTF8.GetString(v.Serialize());

        Assert.DoesNotContain(scan.Data, blob);
        Assert.DoesNotContain("passport.jpg", blob);
    }

    [Fact] // 3
    public void EditingARecord_KeepsItsAttachments()
    {
        var v = Vault.Create(Pw, Fast);
        string id = v.Add(Doc(Scan()));

        VaultItem edited = v.Get(id);
        edited.Title = "Паспорт РФ";
        v.Update(id, edited);

        Assert.Single(v.Get(id).Attachments);
    }

    [Fact] // 4
    public void VaultWithAttachments_ClimbsToItsOwnFormat()
    {
        // A build that cannot show a scan must refuse the file outright rather than be the one
        // that quietly deletes it — same rule the recovery envelope follows.
        var v = Vault.Create(Pw, Fast);
        v.Add(new VaultItem { Type = "note", Title = "Просто заметка" });
        Assert.Equal(1, FormatOf(v.Serialize()));

        string id = v.Add(Doc(Scan()));
        Assert.Equal(3, FormatOf(v.Serialize()));

        // ...and drops back once the last attachment is gone
        VaultItem stripped = v.Get(id);
        stripped.Attachments.Clear();
        v.Update(id, stripped);
        Assert.Equal(1, FormatOf(v.Serialize()));
    }

    [Fact] // 5
    public void DeletingTheRecord_DropsTheFormatBackToo()
    {
        var v = Vault.Create(Pw, Fast);
        string id = v.Add(Doc(Scan()));
        v.Add(new VaultItem { Type = "note", Title = "Заметка" });
        Assert.Equal(3, FormatOf(v.Serialize()));

        v.Delete(id);

        Assert.Equal(1, FormatOf(v.Serialize()));
    }

    [Fact] // 6
    public void OversizedAttachment_IsRefused()
    {
        var v = Vault.Create(Pw, Fast);
        var huge = Scan("scan.jpg", Vault.MaxAttachmentBytes + 1);

        Assert.Throws<AttachmentTooLargeException>(() => v.Add(Doc(huge)));
    }

    [Fact] // 7
    public void TooManyAttachments_AreRefused()
    {
        var v = Vault.Create(Pw, Fast);
        var many = Enumerable.Range(0, Vault.MaxAttachmentsPerItem + 1)
                             .Select(i => Scan($"page{i}.jpg", 128)).ToArray();

        Assert.Throws<AttachmentTooLargeException>(() => v.Add(Doc(many)));
    }

    [Fact] // 8
    public void DeclaredSizeIsNotTrusted_OnlyTheActualPayload()
    {
        // A hand-edited vault could claim a tiny size for a huge blob.
        var v = Vault.Create(Pw, Fast);
        var lying = Scan("scan.jpg", Vault.MaxAttachmentBytes + 1);
        lying.Bytes = 10;

        Assert.Throws<AttachmentTooLargeException>(() => v.Add(Doc(lying)));
    }

    [Fact] // 9
    public void CorruptBase64_IsRefusedRatherThanStored()
    {
        var v = Vault.Create(Pw, Fast);
        var broken = Scan();
        broken.Data = "не-base64!!";

        Assert.Throws<AttachmentTooLargeException>(() => v.Add(Doc(broken)));
    }

    [Fact] // 10
    public void UnknownFieldsFromANewerBuild_SurviveAnEdit()
    {
        // The whole point of JsonExtensionData: an older app must not erase what a newer one
        // wrote just because it opened the record and saved it.
        var v = Vault.Create(Pw, Fast);
        string id = v.Add(new VaultItem { Type = "account", Title = "Тест" });

        VaultItem item = v.Get(id);
        item.Extra = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            """{"fieldFromTheFuture":"важное значение"}""");
        v.Update(id, item);

        VaultItem after = Vault.Unlock(v.Serialize(), Pw).Get(id);

        Assert.NotNull(after.Extra);
        Assert.Equal("важное значение", after.Extra!["fieldFromTheFuture"].GetString());
    }

    [Fact] // 11
    public void AttachmentsRideAlongOnASyncMerge()
    {
        var pc = Vault.Create(Pw, Fast);
        var phone = Vault.Unlock(pc.Serialize(), Pw);

        string id = pc.Add(Doc(Scan()));
        phone.MergeFrom(pc.Serialize());

        Assert.Single(phone.Get(id).Attachments);
        Assert.Equal(3, FormatOf(phone.Serialize()));
    }

    private static int FormatOf(byte[] blob) => (int)JsonNode.Parse(blob)!["format"]!;
}
