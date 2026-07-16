using System.Windows;

namespace OctoConverter;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            MessageBox.Show(
                "예상치 못한 오류가 발생했습니다:\n\n" + e.Exception.Message,
                "OctoConverter", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };
    }
}
