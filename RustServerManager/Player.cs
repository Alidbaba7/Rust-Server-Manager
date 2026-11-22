using Newtonsoft.Json;

namespace RustServerManager
{
    public class Player
    {
        [JsonProperty("SteamID")]
        public string? SteamId { get; set; }

        [JsonProperty("Name")]
        public string? Name { get; set; }

        [JsonProperty("Ping")]
        public string? Ping { get; set; }

        [JsonProperty("ConnectedSeconds")]
        public string? ConnectedSeconds { get; set; }
    }
}