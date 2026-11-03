using System.ComponentModel.DataAnnotations;

namespace PetCore.Api.Models;

public sealed class Device
{
    public int Id { get; set; }
    [MaxLength(100)] public required string Identifier { get; set; }
    [MaxLength(160)] public string Name { get; set; } = "PetCore ESP32";
    [MaxLength(30)] public string Status { get; set; } = "Sem comunicação";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSeenAt { get; set; }
    public List<Measurement> Measurements { get; set; } = [];
}

public sealed class Measurement
{
    public long Id { get; set; }
    public int DeviceId { get; set; }
    public Device? Device { get; set; }
    [MaxLength(80)] public required string Sensor { get; set; }
    [MaxLength(200)] public required string Value { get; set; }
    [MaxLength(40)] public string Unit { get; set; } = "estado";
    public DateTimeOffset? MeasuredAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
    [MaxLength(80)] public string Origin { get; set; } = "Node-RED/MQTT";
    [MaxLength(128)] public required string Fingerprint { get; set; }
    public string OriginalData { get; set; } = "{}";
    public AiAnalysis? Analysis { get; set; }
}

public sealed class AiModel
{
    public int Id { get; set; }
    [MaxLength(100)] public required string Name { get; set; }
    [MaxLength(100)] public required string Algorithm { get; set; }
    [MaxLength(40)] public required string Version { get; set; }
    public string Parameters { get; set; } = "{}";
    public DateTimeOffset? TrainedAt { get; set; }
    [MaxLength(30)] public string Status { get; set; } = "Coletando dados";
    public List<AiAnalysis> Analyses { get; set; } = [];
}

public sealed class AiAnalysis
{
    public long Id { get; set; }
    public long MeasurementId { get; set; }
    public Measurement? Measurement { get; set; }
    public int? ModelId { get; set; }
    public AiModel? Model { get; set; }
    [MaxLength(40)] public required string Result { get; set; }
    public double? Score { get; set; }
    [MaxLength(500)] public required string Explanation { get; set; }
    public DateTimeOffset AnalyzedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Alert
{
    public long Id { get; set; }
    public long AnalysisId { get; set; }
    public AiAnalysis? Analysis { get; set; }
    public long MeasurementId { get; set; }
    public Measurement? Measurement { get; set; }
    [MaxLength(160)] public required string Title { get; set; }
    [MaxLength(500)] public required string Message { get; set; }
    [MaxLength(20)] public string Priority { get; set; } = "informativo";
    [MaxLength(30)] public string Status { get; set; } = "aberto";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record IngestRequest(string DeviceId, string Topic, string Value, string? EventTime, string? EventId, Dictionary<string, object?>? Data);
