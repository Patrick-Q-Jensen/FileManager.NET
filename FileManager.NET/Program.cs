
using Serilog;
using Terminal.Gui.App;
using FileManager.NET.Core.Favorites;
using FileManager.NET.Core.FileSystem;
using FileManager.NET.Core.Filtering;
using FileManager.NET.Core.Sorting;
using FileManager.NET.Platform;
using FileManager.NET.Ui;

namespace FileManager.NET
{
    internal static class Program
    {
        private static void Main(string[] args)
        {
            ConfigureLogging();

            // Belt-and-braces: make sure a truly unhandled exception on any thread is recorded
            // before the process goes down, since the app must never crash silently.
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception (IsTerminating={IsTerminating})", e.IsTerminating);

            try
            {
                Log.Information("File Manager starting up.");

                string startDirectory = args.Length > 0 && Directory.Exists(args[0])
                    ? Path.GetFullPath(args[0])
                    : Environment.CurrentDirectory;

                IDirectoryService directoryService = new DirectoryService();
                IEntryFilter filter = new SubstringEntryFilter();
                IFileLauncher launcher = new WindowsFileLauncher();

                // Start loading favorites from disk in the background so it never delays startup.
                IFavoritesService favorites = new FavoritesService();
                favorites.BeginLoad();

                // Loaded synchronously: the global sort order must be known before the first
                // directory listing is sorted, and the settings file is tiny.
                ISortSettingsService sortSettings = new SortSettingsService();

                Console.Title = "File Manager";

                // Disposing the application restores the terminal (alternate buffer, cursor),
                // and `using` guarantees that even if Run throws.
                using IApplication app = Application.Create();
                app.Init();

                using FileManagerTabs tabs = new FileManagerTabs(app, directoryService, filter, launcher, favorites, sortSettings, startDirectory);
                app.Run(tabs);
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "File Manager terminated unexpectedly.");
            }
            finally
            {
                Log.Information("File Manager shutting down.");
                Log.CloseAndFlush();
            }
        }

        // Writes daily rolling log files to a Logs folder next to the executable. Kept to a
        // bounded retention so the app never accumulates unbounded disk usage over time.
        private static void ConfigureLogging()
        {
            string logDirectory = Path.Combine(AppContext.BaseDirectory, "Logs");

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.File(
                    Path.Combine(logDirectory, "log-.txt"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14)
                .CreateLogger();
        }
    }
}

