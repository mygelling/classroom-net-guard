using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClassroomNetGuard.Shared
{
    /// <summary>统一消息壳：{"t":"消息类型","data":{...}}。</summary>
    public sealed class WireMessage
    {
        public const string THello = "hello";
        public const string THeartbeat = "heartbeat";
        public const string TLog = "log";
        public const string TPolicy = "policy";
        public const string TAck = "ack";
        public const string TCmd = "cmd";

        [JsonPropertyName("t")]
        public string T { get; set; }

        [JsonPropertyName("data")]
        public JsonElement Data { get; set; }

        public static WireMessage Create(string t, object data)
            => new WireMessage { T = t, Data = JsonSerializer.SerializeToElement(data, JsonOpts.Options) };

        public static WireMessage CreatePlain(string t)
            => new WireMessage { T = t, Data = default };

        public string Serialize() => JsonSerializer.Serialize(this, JsonOpts.Options);

        public static WireMessage Parse(string json)
            => JsonSerializer.Deserialize<WireMessage>(json, JsonOpts.Options);

        public T DataAs<T>() => Data.Deserialize<T>(JsonOpts.Options);
    }

    public static class JsonOpts
    {
        public static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    // ===== 学生端 -> 教师端 =====

    public sealed class HelloData
    {
        public string Seat { get; set; }
        public string Name { get; set; }
        public string Ip { get; set; }
        public string Os { get; set; }
    }

    public sealed class HeartbeatData
    {
        public long Tick { get; set; }
        public string CurrentUrl { get; set; }
        public int BlockedCount { get; set; }
        public int PolicyVersion { get; set; }
    }

    public sealed class LogData
    {
        public string Device { get; set; }
        public string Event { get; set; }
        public string Result { get; set; }
    }

    public sealed class AckData
    {
        public int Version { get; set; }
    }

    // ===== 教师端 -> 学生端 =====

    public sealed class CmdData
    {
        public string Action { get; set; }
        public string Param { get; set; }
    }
}
