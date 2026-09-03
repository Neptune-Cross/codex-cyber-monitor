using CodexCyberMonitor.App;
using CodexCyberMonitor.Infrastructure;

namespace CodexCyberMonitor;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
        {
            return SelfTest.Run();
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        try
        {
            using var singleInstance = new SingleInstanceCoordinator();
            var activationCommand = InstanceActivationCommandExtensions.FromArguments(args);
            if (!singleInstance.IsFirstInstance)
            {
                if (activationCommand == InstanceActivationCommand.None)
                {
                    return 0;
                }

                if (singleInstance.TrySendActivation(activationCommand, TimeSpan.FromSeconds(3)))
                {
                    return 0;
                }

                MessageBox.Show(
                    "Codex Cyber 实时监测器已在后台运行，但未能激活前台面板。\n请从 Windows 托盘的红绿盾牌图标打开。",
                    "Codex Cyber 实时监测器",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return 1;
            }

            using var context = new CyberMonitorApplicationContext(args);
            Application.ThreadException += (_, eventArgs) => context.ReportUnhandledException(eventArgs.Exception);
            AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            {
                if (eventArgs.ExceptionObject is Exception exception)
                {
                    context.ReportUnhandledException(exception);
                }
            };

            singleInstance.StartListening(
                context.HandleActivationCommand,
                exception => context.ReportUnhandledException(
                    new InvalidOperationException("单实例激活通道异常。", exception)));
            try
            {
                Application.Run(context);
            }
            finally
            {
                singleInstance.StopListening();
            }

            return 0;
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"启动监测器失败：\n{exception.GetType().Name}: {exception.Message}",
                "Codex Cyber 实时监测器",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
        }
    }
}
