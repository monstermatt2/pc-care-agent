using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PcCareAgent;

public sealed class Worker : BackgroundService
{
    public const string AgentVersion = "0.9.8";

    private readonly ApiClient _api;
    private readonly AgentConfig _config;
    private readonly ILogger<Worker> _log;

    private DefenderFacts? _defender;
    private ServiceFacts? _services;
    private DateTime _lastDefenderQuery = DateTime.MinValue;
    private DateTime _lastDiagnose = DateTime.MinValue;
    private DateTime _lastToast = DateTime.MinValue;
    private readonly HashSet<string> _lastSeenIssues = new();
    private List<string> _survey = new();
    private readonly HashSet<string> _lastSurveySeen = new();
    private bool _forceDiagnose;
    private readonly List<TaskOutcome> _recentResults = new();

    public Worker(ApiClient api, AgentConfig config, ILogger<Worker> log)
    {
        _api = api;
        _config = config;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_config.DeviceToken is null)
        {
            _log.LogError(
                "Agent is not registered. Run as admin: PcCareAgent.exe register --server {Server} --token <enroll-token>",
                _config.ServerUrl);
            return;
        }

        var lastUpdateCheck = DateTime.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // 1. health check-in
                var snap = SystemInfo.Snapshot(AgentVersion);
                await _api.CheckInAsync(snap, stoppingToken);
                _log.LogInformation("Checked in: {Hostname} ({Os}), uptime {Uptime}h",
                    snap.Hostname, snap.Os, snap.UptimeHours);

                // 2. drain task queue
                while (await _api.GetNextTaskAsync(stoppingToken) is { } task)
                {
                    _log.LogInformation("Running task {Id} ({Type})", task.Id, task.Type);
                    var (status, result) = await TaskRunner.RunAsync(task, _api, stoppingToken);
                    await _api.ReportTaskAsync(task.Id, status, result, stoppingToken);

                    _recentResults.Add(new TaskOutcome(task.Type, System.Text.Json.JsonSerializer.Serialize(result)));
                    if (_recentResults.Count > 6) _recentResults.RemoveAt(0);

                    if (result is DefenderScanOutcome o)
                        _defender = DefenderFacts.Parse(o.DefenderStatusJson) ?? _defender;

                    // a repair just ran -> refresh facts and post an all-clear diagnosis next cycle
                    if (task.Type is "service_enable_wu" or "defender_enable_rtp" or "service_start")
                    {
                        _forceDiagnose = true;
                        _lastDefenderQuery = DateTime.MinValue;
                    }

                    _log.LogInformation("Task {Type} finished: {Status}", task.Type, status);
                }

                // 3. AI brain — the agent prompts ITSELF when it finds issues (max 1/hour;
                //    also runs once on first cycle so every machine gets an initial verdict)
                if (DateTime.UtcNow - _lastDefenderQuery > TimeSpan.FromMinutes(10))
                {
                    _lastDefenderQuery = DateTime.UtcNow;
                    _defender = TaskRunner.QueryDefender() ?? _defender;
                    _services = TaskRunner.QueryServices() ?? _services;
                    _survey = TaskRunner.QuerySurvey();
                    foreach (var s in _survey) _log.LogInformation("Survey: {Signal}", s);
                }
                var issues = IssueDetector.Detect(snap, _defender, _services);
                foreach (var issue in issues) _log.LogWarning("Issue detected: {Issue}", issue);

                var firstCycle = _lastDiagnose == DateTime.MinValue;
                var setChanged = !issues.ToHashSet().SetEquals(_lastSeenIssues);
                var surveyChanged = !_survey.ToHashSet().SetEquals(_lastSurveySeen);
                if ((firstCycle || setChanged || surveyChanged || _forceDiagnose)
                    && DateTime.UtcNow - _lastDiagnose > TimeSpan.FromMinutes(5))
                {
                    _lastDiagnose = DateTime.UtcNow;
                    _forceDiagnose = false;
                    _lastSeenIssues.Clear();
                    foreach (var i in issues) _lastSeenIssues.Add(i);
                    _lastSurveySeen.Clear();
                    foreach (var s in _survey) _lastSurveySeen.Add(s);
                    try
                    {
                        var d = await _api.DiagnoseAsync(snap, issues, _survey, _recentResults, stoppingToken);
                        if (d is not null)
                        {
                            _log.LogInformation("AI diagnosis [{Severity}]: {Summary}", d.Severity, d.Summary);
                            foreach (var r in d.Recommendations ?? new List<Recommendation>())
                                _log.LogInformation("  AI recommends: {Action} — {Reason}", r.Action, r.Reason);

                            // serious verdict -> "tap to fix" toast in the owner's session (max 1/30min)
                            // survey findings have no local detector — toast those too (issues may be empty).
                            // ToastGate is PERSISTED: a service crash/restart loop must not spam toasts.
                            if ((d.Severity is "warning" or "critical") &&
                                (issues.Count > 0 || _survey.Count > 0) &&
                                ToastGate.Allow())
                            {
                                ToastGate.Mark();
                                _lastToast = DateTime.UtcNow;
                                var issueLine = issues.Count > 0 ? issues[0] : "Windows needs attention";
                                SessionLauncher.ShowToast(issueLine, d.Summary);
                                _log.LogInformation("Repair toast shown: {Issue}", issueLine);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "AI diagnosis failed (server may not have GLM_API_KEY set)");
                        _lastDiagnose = DateTime.UtcNow.AddMinutes(-45); // retry in ~15 min
                    }
                }

                // 4. self-update check
                if (DateTime.UtcNow - lastUpdateCheck > TimeSpan.FromSeconds(Math.Max(60, _config.UpdateIntervalSec)))
                {
                    lastUpdateCheck = DateTime.UtcNow;
                    if (await Updater.TryUpdateAsync(_api, _log, stoppingToken))
                    {
                        _log.LogInformation("Update staged — exiting so the service restarts on the new binary.");
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Cycle failed; retrying next interval");
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Max(10, _config.CheckIntervalSec)), stoppingToken);
        }
    }
}
