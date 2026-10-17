using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace RomM.Models.RomM.Save
{
    /// <summary>
    /// A single locally-known save reported to POST /sync/negotiate. The server keys saves by
    /// (rom_id, slot) and compares <see cref="ContentHash"/> (MD5 hex) then <see cref="UpdatedAt"/>
    /// against its own copy to decide the sync action.
    /// </summary>
    public class RomMClientSaveState
    {
        [JsonProperty("rom_id")]
        public int RomId { get; set; }

        [JsonProperty("file_name")]
        public string FileName { get; set; }

        [JsonProperty("slot")]
        public string Slot { get; set; }

        [JsonProperty("emulator")]
        public string Emulator { get; set; }

        [JsonProperty("content_hash")]
        public string ContentHash { get; set; }

        [JsonProperty("updated_at")]
        public DateTime UpdatedAt { get; set; }

        [JsonProperty("file_size_bytes")]
        public long FileSizeBytes { get; set; }
    }

    public class RomMSyncNegotiatePayload
    {
        [JsonProperty("device_id")]
        public string DeviceId { get; set; }

        [JsonProperty("saves")]
        public List<RomMClientSaveState> Saves { get; set; } = new List<RomMClientSaveState>();

        /// <summary>
        /// The ROMs this negotiation is about. Keeps the server from listing every save in the
        /// library when only one game is being synced.
        /// </summary>
        [JsonProperty("rom_ids", NullValueHandling = NullValueHandling.Ignore)]
        public List<int> RomIds { get; set; }

        /// <summary>
        /// Asks for a server save this device no longer has, even one it synced before. Without
        /// it the server reads a missing local save as a deliberate deletion and offers nothing,
        /// so a save lost with a wiped or reinstalled emulator would never come back.
        /// </summary>
        [JsonProperty("restore_unlisted")]
        public bool RestoreUnlisted { get; set; }
    }

    /// <summary>A save as the server lists it (GET /api/saves); only the fields a restore needs.</summary>
    public class RomMServerSave
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("slot")]
        public string Slot { get; set; }

        [JsonProperty("emulator")]
        public string Emulator { get; set; }

        [JsonProperty("updated_at")]
        public System.DateTime? UpdatedAt { get; set; }
    }

    /// <summary>The action the server wants the client to perform for a given save.</summary>
    public static class RomMSyncAction
    {
        public const string Upload = "upload";
        public const string Download = "download";
        public const string Conflict = "conflict";
        public const string NoOp = "no_op";
    }

    public class RomMSyncOperation
    {
        [JsonProperty("action")]
        public string Action { get; set; }

        [JsonProperty("rom_id")]
        public int RomId { get; set; }

        [JsonProperty("save_id")]
        public int? SaveId { get; set; }

        [JsonProperty("file_name")]
        public string FileName { get; set; }

        [JsonProperty("slot")]
        public string Slot { get; set; }

        [JsonProperty("emulator")]
        public string Emulator { get; set; }

        [JsonProperty("reason")]
        public string Reason { get; set; }

        [JsonProperty("server_updated_at")]
        public DateTime? ServerUpdatedAt { get; set; }

        [JsonProperty("server_content_hash")]
        public string ServerContentHash { get; set; }
    }

    public class RomMSyncNegotiateResponse
    {
        [JsonProperty("session_id")]
        public int SessionId { get; set; }

        [JsonProperty("operations")]
        public List<RomMSyncOperation> Operations { get; set; } = new List<RomMSyncOperation>();

        [JsonProperty("total_upload")]
        public int TotalUpload { get; set; }

        [JsonProperty("total_download")]
        public int TotalDownload { get; set; }

        [JsonProperty("total_conflict")]
        public int TotalConflict { get; set; }

        [JsonProperty("total_no_op")]
        public int TotalNoOp { get; set; }
    }

    /// <summary>Payload for POST /sync/sessions/{id}/complete. play_sessions is omitted (null).</summary>
    public class RomMSyncCompletePayload
    {
        [JsonProperty("operations_completed")]
        public int OperationsCompleted { get; set; }

        [JsonProperty("operations_failed")]
        public int OperationsFailed { get; set; }
    }
}
