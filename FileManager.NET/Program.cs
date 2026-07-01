using Terminal.Gui.App;
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
            var startDirectory = args.Length > 0 && Directory.Exists(args[0])
                ? Path.GetFullPath(args[0])
                : Environment.CurrentDirectory;

            IDirectoryService directoryService = new DirectoryService();
            IEntryFilter filter = new SubstringEntryFilter();
            IFileLauncher launcher = new WindowsFileLauncher();
            var controller = new NavigationController(directoryService, filter, launcher);

            Console.Title = "FileManager";

            // Disposing the application restores the terminal (alternate buffer, cursor),
            // and `using` guarantees that even if Run throws.
            using IApplication app = Application.Create();
            app.Init();

            using var window = new FileManagerWindow(app, controller, startDirectory);
            app.Run(window);
        }
    }
}

