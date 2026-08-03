namespace DisplayControl.Agent;

static class Program
{
    /// <summary>
    ///  The main entry point for the application.
    /// </summary>
    [STAThread]
    static void Main()
    {
        // To customize application configuration such as set high DPI settings or default font,
        // see https://aka.ms/applicationconfiguration.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, args) => LogCrash(args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                LogCrash(exception);
            }
        };
        ApplicationConfiguration.Initialize();
        Application.Run(new Form1());

        static void LogCrash(Exception exception)
        {
            try
            {
                var logPath = Path.Combine(AppContext.BaseDirectory, "agent-crash.log");
                File.AppendAllText(
                    logPath,
                    $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {exception}\r\n\r\n");
            }
            catch
            {
                // 最后的兜底日志不能再抛异常，避免二次崩溃。
            }
        }
    }
}
