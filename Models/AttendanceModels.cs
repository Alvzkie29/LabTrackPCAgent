namespace LabTrackAgent.Models;

/// <summary>Sent to POST api/auth/login (the same endpoint the web dashboard uses).</summary>
public class LoginRequest
{
    public string Identifier { get; set; } = string.Empty; // student ID or email
    public string Password { get; set; } = string.Empty;
}

public class AuthResponse
{
    public string Token { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public List<string> Roles { get; set; } = new();
    public bool MustChangePassword { get; set; }
}

/// <summary>One class that can be chosen on the confirm screen.</summary>
public class SectionOption
{
    public int SectionId { get; set; }
    public string SectionCode { get; set; } = string.Empty;
    public string SubjectCode { get; set; } = string.Empty;
    public string SubjectTitle { get; set; } = string.Empty;
    public TimeSpan StartTime { get; set; }
    public TimeSpan EndTime { get; set; }

    public string Display =>
        $"{SubjectCode} - {SubjectTitle} ({DateTime.Today.Add(StartTime):h:mm tt} to {DateTime.Today.Add(EndTime):h:mm tt})";
}

/// <summary>Response of GET api/agent/options: the suggested class plus the other classes in this lab today.</summary>
public class AttendanceOptions
{
    public SectionOption? Suggested { get; set; }
    public List<SectionOption> Alternatives { get; set; } = new();
}

public class ConfirmRequest
{
    public string PcIdentifier { get; set; } = string.Empty;
    public int SectionId { get; set; }
}

public class ConfirmResponse
{
    public int AttendanceId { get; set; }
    public bool AlreadyRecorded { get; set; }
}
public class ChangePasswordRequest
{
    public string CurrentPassword { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
}

public class MessageResponse
{
    public string? Message { get; set; }
}