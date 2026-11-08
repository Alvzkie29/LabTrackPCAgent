using System.IO;
using System.Text.Json;

namespace LabTrackAgent;

/// <summary>
/// Per-machine configuration for the agent. Loaded from a small JSON file
/// dropped next to the executable at install time (see PC 3 setup step
/// in our deployment plan — the installer prompts for pc_identifier and
/// writes this file once per machine).
///
/// Example agent.config.json:
/// {
///   "ApiBaseUrl": "https://labtrack.example.edu/api",
///   "PcIdentifier": "CL1-PC07",
///   "LabId": 1
/// }
/// </summary>
public class AgentConfig
{
    public string ApiBaseUrl { get; set; } = "https://localhost:7173/api";
    public string PcIdentifier { get; set; } = "CL1-PC01";
    public int LabId { get; set; } = 1;
    public bool LockEnabled { get; set; } = false;

    private const string ConfigFileName = "agent.config.json";

    public static AgentConfig Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, ConfigFileName);

        if (!File.Exists(path))
        {
            // No config yet — this should only happen on a dev machine
            // before the installer has run. Write a default one so the
            // app doesn't crash outright, but this PC won't be able to
            // report attendance meaningfully until it's configured.
            var fallback = new AgentConfig();
            File.WriteAllText(path, JsonSerializer.Serialize(fallback, new JsonSerializerOptions { WriteIndented = true }));
            return fallback;
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<AgentConfig>(json) ?? new AgentConfig();
    }
}
