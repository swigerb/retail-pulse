namespace RetailPulse.Api.Alerts;

/// <summary>
/// SQLite DDL for the proactive alerts subsystem.
/// </summary>
internal static class AlertDbSchema
{
    public const string CreateTables = """
        PRAGMA journal_mode=WAL;

        CREATE TABLE IF NOT EXISTS Alerts (
            Id                TEXT PRIMARY KEY,
            Type              TEXT NOT NULL,
            Severity          TEXT NOT NULL,
            Title             TEXT NOT NULL,
            Description       TEXT,
            Brand             TEXT,
            Region            TEXT,
            RecommendedAction TEXT,
            DetectedAt        TEXT NOT NULL,
            Metadata          TEXT
        );

        CREATE INDEX IF NOT EXISTS IX_Alerts_DetectedAt ON Alerts (DetectedAt DESC);
        CREATE INDEX IF NOT EXISTS IX_Alerts_TypeBrandRegion ON Alerts (Type, Brand, Region);

        CREATE TABLE IF NOT EXISTS AlertThrottles (
            Type         TEXT NOT NULL,
            Brand        TEXT NOT NULL,
            Region       TEXT NOT NULL,
            LastFiredAt  TEXT NOT NULL,
            PRIMARY KEY (Type, Brand, Region)
        );

        CREATE TABLE IF NOT EXISTS AlertSnoozes (
            UserId       TEXT NOT NULL,
            AlertType    TEXT NOT NULL,
            Brand        TEXT,
            Region       TEXT,
            SnoozedUntil TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS IX_AlertSnoozes_User ON AlertSnoozes (UserId, AlertType);

        CREATE TABLE IF NOT EXISTS AlertDismissals (
            AlertId TEXT NOT NULL,
            UserId  TEXT NOT NULL,
            DismissedAt TEXT NOT NULL,
            PRIMARY KEY (AlertId, UserId)
        );
        """;
}
