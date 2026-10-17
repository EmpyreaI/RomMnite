using Newtonsoft.Json;

namespace RomM.Models.RomM.Rom
{
    // Screenscraper metadata/media on a rom. Only the media we consume (game icon and background) are
    // modelled here; Newtonsoft ignores the rest of the object.
    public class RomMSSMetadata
    {
        // Composite image with consistent dimensions. _path is served by RomM (relative), _url is external.
        [JsonProperty("miximage_path")]
        public string MiximagePath { get; set; }
        [JsonProperty("miximage_url")]
        public string MiximageUrl { get; set; }

        // Landscape fan art, used as the game background. Same _path/_url split as the miximage;
        // _path is only present when RomM is configured to store fan art locally.
        [JsonProperty("fanart_path")]
        public string FanartPath { get; set; }
        [JsonProperty("fanart_url")]
        public string FanartUrl { get; set; }

        // Transparent game logo ("wheel" art), saved for the ExtraMetadataLoader extension.
        [JsonProperty("logo_path")]
        public string LogoPath { get; set; }
        [JsonProperty("logo_url")]
        public string LogoUrl { get; set; }

        // External Screenscraper screenshot, the last-resort background.
        [JsonProperty("screenshot_url")]
        public string ScreenshotUrl { get; set; }

        // Flat box art, front and back, shown under the description.
        [JsonProperty("box2d_path")]
        public string Box2dPath { get; set; }
        [JsonProperty("box2d_url")]
        public string Box2dUrl { get; set; }
        [JsonProperty("box2d_back_path")]
        public string Box2dBackPath { get; set; }
        [JsonProperty("box2d_back_url")]
        public string Box2dBackUrl { get; set; }

        // Gameplay video kept on the RomM server, streamed in the details view. Only the _path
        // forms are modelled: a video RomM does not hold itself is deliberately never played.
        [JsonProperty("video_path")]
        public string VideoPath { get; set; }
        [JsonProperty("video_normalized_path")]
        public string VideoNormalizedPath { get; set; }
    }
}
