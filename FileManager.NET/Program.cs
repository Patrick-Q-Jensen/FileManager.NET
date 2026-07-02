using Terminal.Gui.App;
using FileManager.NET.Core.Favorites;
using FileManager.NET.Core.FileSystem;
using FileManager.NET.Core.Filtering;
using FileManager.NET.Core.Navigation;
using FileManager.NET.Platform;
using FileManager.NET.Ui;

namespace FileManager.NET
{
    internal static class Program
    {
        private static void Main(string[] args)
        {
            string startDirectory = args.Length > 0 && Directory.Exists(args[0])
                ? Path.GetFullPath(args[0])
                : Environment.CurrentDirectory;

            IDirectoryService directoryService = new DirectoryService();
            IEntryFilter filter = new SubstringEntryFilter();
            IFileLauncher launcher = new WindowsFileLauncher();
            NavigationController controller = new NavigationController(directoryService, filter, launcher);

            // Start loading favorites from disk in the background so it never delays startup.
            IFavoritesService favorites = new FavoritesService();
            favorites.BeginLoad();

            Console.Title = "FileManager";

            // Disposing the application restores the terminal (alternate buffer, cursor),
            // and `using` guarantees that even if Run throws.
            using IApplication app = Application.Create();
            app.Init();

            using FileManagerWindow window = new FileManagerWindow(app, controller, favorites, startDirectory);
            app.Run(window);
        }
    }
}

