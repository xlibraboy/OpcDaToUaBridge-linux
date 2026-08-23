namespace OpcBridge.Core;

public sealed class MqttBrokerOptions
{
    public bool Enabled { get; set; }
    public string BrokerUrl { get; set; } = "tcp://localhost:1883";
    public string ClientId { get; set; } = "OpcBridge";
    public string? UserName { get; set; }
    public string? Password { get; set; }
    public bool Tls { get; set; }
    public bool IgnoreCertErrors { get; set; }
    public string TopicPrefix { get; set; } = "bridge/tags";
    public MqttPayloadField PayloadFields { get; set; } = MqttPayloadField.Value | MqttPayloadField.Timestamp;
}
