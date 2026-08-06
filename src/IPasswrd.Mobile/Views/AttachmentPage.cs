using IPasswrd.Mobile.Services;

namespace IPasswrd.Mobile.Views;

/// <summary>
/// Просмотр картинки-вложения. Байты держим в памяти и не пишем на диск: скан паспорта,
/// оставленный во временной папке, пережил бы и блокировку сейфа, и удаление записи.
/// «Поделиться» — единственный выход наружу, и он осознанный.
/// </summary>
public sealed class AttachmentPage : ContentPage
{
    private readonly byte[] _data;
    private readonly string _name;

    public AttachmentPage(string name, byte[] data)
    {
        _name = name;
        _data = data;
        Title = name;
        BackgroundColor = Colors.Black;

        var image = new Image
        {
            Source = ImageSource.FromStream(() => new MemoryStream(_data)),
            Aspect = Aspect.AspectFit,
            VerticalOptions = LayoutOptions.Center,
        };

        // Двойное касание — вписать/увеличить: разглядывать номер на скане иначе неудобно.
        var zoom = new TapGestureRecognizer { NumberOfTapsRequired = 2 };
        zoom.Tapped += (_, _) => image.Aspect = image.Aspect == Aspect.AspectFit ? Aspect.AspectFill : Aspect.AspectFit;
        image.GestureRecognizers.Add(zoom);

        Content = new ScrollView { Content = image };

        ToolbarItems.Add(new ToolbarItem { Text = "Поделиться", Command = new Command(async () => await ShareAsync()) });
    }

    private async Task ShareAsync()
    {
        string path = Path.Combine(FileSystem.CacheDirectory, Attachments.SafeFileName(_name));
        try
        {
            await File.WriteAllBytesAsync(path, _data);
            await Share.Default.RequestAsync(new ShareFileRequest { Title = _name, File = new ShareFile(path) });
        }
        catch (Exception)
        {
            await DisplayAlert("Не получилось", "Файл не отдался системе.", "Ок");
        }
    }
}
