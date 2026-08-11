using IPasswrd.Core;

namespace IPasswrd.Mobile.Services;

/// <summary>
/// Выбор фотографии или файла и подготовка вложения — один кусок на карточку записи и на форму
/// создания.
///
/// Разошлись бы они на первой же правке: тут поправили сообщение о размере, там забыли; тут
/// добавили «снять фото», там осталось только «файл». Весь разговор с человеком — лист выбора,
/// отказ камеры, слишком большой файл — живёт здесь; куда положить готовое вложение, решает
/// вызывающий: на карточке оно сохраняется сразу, в форме создания — вместе с записью.
/// </summary>
public static class AttachmentPick
{
    /// <summary>
    /// null — человек передумал или что-то не вышло; про неудачу он уже предупреждён,
    /// вызывающему остаётся просто ничего не делать.
    /// </summary>
    public static async Task<Attachment?> PickAsync(Page page, int already)
    {
        if (already >= Vault.MaxAttachmentsPerItem)
        {
            await page.DisplayAlert("Больше не поместится",
                $"В одной записи не больше {Vault.MaxAttachmentsPerItem} вложений.", "Ок");
            return null;
        }

        const string shoot = "Снять фото";
        const string library = "Из фотоплёнки";
        const string file = "Файл";
        string choice = await page.DisplayActionSheet("Добавить вложение", "Отмена", null, shoot, library, file);

        FileResult? picked;
        try
        {
            if (choice == shoot)
            {
                if (!MediaPicker.Default.IsCaptureSupported)
                {
                    await page.DisplayAlert("Не получилось", "Камера недоступна.", "Ок");
                    return null;
                }
                picked = await MediaPicker.Default.CapturePhotoAsync();
            }
            else if (choice == library) picked = await MediaPicker.Default.PickPhotoAsync();
            else if (choice == file) picked = await FilePicker.Default.PickAsync();
            else return null;
        }
        catch (Exception)
        {
            await page.DisplayAlert("Не получилось",
                DeviceInfo.Platform == DevicePlatform.iOS
                    ? "Нет доступа к камере или фотографиям. Разрешение выдаётся в Настройках iPhone → IPasswrd."
                    : "Нет доступа к камере или фотографиям. Разрешение выдаётся в Настройках → Приложения → IPasswrd → Разрешения.", "Ок");
            return null;
        }

        if (picked is null) return null;

        byte[] raw;
        try
        {
            await using Stream src = await picked.OpenReadAsync();
            using var ms = new MemoryStream();
            await src.CopyToAsync(ms);
            raw = ms.ToArray();
        }
        catch (Exception)
        {
            await page.DisplayAlert("Не получилось", "Файл не прочитался.", "Ок");
            return null;
        }

        try { return Attachments.Prepare(picked.FileName ?? "файл", raw); }
        catch (AttachmentTooLargeException ex)
        {
            await page.DisplayAlert("Слишком большой файл", ex.Message, "Ок");
            return null;
        }
        catch (Exception)
        {
            await page.DisplayAlert("Не получилось", "Файл не подготовился.", "Ок");
            return null;
        }
    }
}
