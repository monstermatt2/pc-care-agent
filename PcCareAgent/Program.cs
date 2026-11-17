using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;
using PcCareAgent;

if (args.Length > 0 && args[0] == "register")
{
    return await Registration.RunAsync(args);
}

if (args.Length > 1 && args[0] == "--toast")
{
    ToastHelper.Run(string.Join(' ', args.Skip(1)));
    return 0;
}

if (args.Length > 1 && args[0] == "--apply-update")
{
    ApplyUpdate.Run(args[1]);
    return 0;
}

if (args.Length > 0 && args[0] == "--install-service")
{
    Installer.Run();
    return 0;
}

// Two modes, one exe:
//   - installed as a Windows service -> headless worker (checkins, tasks, AI, updater)
//   - launched by the client (double-click)  -> launcher update check, then the main app
if (OperatingSystem.IsWindows() &&
    !Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService())
{
    Application.EnableVisualStyles();
    Application.SetCompatibleTextRenderingDefault(false);

    // Fresh machine (no config yet): the one-click installer window — paste the
    // install code, it registers + installs the service + launches the app.
    if (AgentConfig.Load().DeviceToken is null)
    {
        Application.Run(new SetupForm());
        return 0; // the elevated installer opens the app from Program Files itself
    }

    // Launcher loop: check (-> download -> install -> relaunch) -> main app.
    // After an install the relaunched process enters here again on the NEW binary,
    // finds no update, and opens the main program.
    while (true)
    {
        UpdateForm.Result = UpdateForm.Outcome.None;
        Application.Run(new UpdateForm());
        if (UpdateForm.Result != UpdateForm.Outcome.OpenMain) break; // applied (exiting) or hard close
        Application.Run(new MainForm());
        if (!MainForm.RestartForUpdate) break;
        MainForm.RestartForUpdate = false; // user tapped the flashing "New update" button -> check again
    }
    return 0;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = AgentConfig.ServiceName);
// Event Log logger defaults to Warning — let agent info logs through for remote debugging
builder.Logging.AddFilter<EventLogLoggerProvider>(level => level >= LogLevel.Information);
builder.Logging.AddFilter("System.Net.Http", LogLevel.Warning); // stop 6 events/request noise
builder.Services.AddSingleton(AgentConfig.Load());
builder.Services.AddHttpClient<ApiClient>();
builder.Services.AddHostedService<Worker>();

builder.Build().Run();
return 0;
