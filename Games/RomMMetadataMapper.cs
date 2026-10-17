using Playnite.SDK.Models;
using RomM.Models.RomM.Rom;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RomM.Games
{
    // Maps a RomM ROM payload to the GameMetadata fields shared by the library importer and the
    // metadata downloader. The importer layers identity/install fields (Source, GameId, Platforms,
    // GameActions, ...) on top of the returned object; the downloader returns it as-is.
    internal static class RomMMetadataMapper
    {
        private static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc);

        // imageAvailable, when given, is asked whether each box art url really serves an image; one
        // that doesn't is left out of the description. Without it every listed url is trusted.
        public static GameMetadata BuildBaseMetadata(RomMRom rom, string romMHost, string preferredRatingsBoard, Func<string, bool> imageAvailable = null)
        {
            var ageRatings = rom.Metadatum.Age_Ratings.Count > 0
                ? new HashSet<MetadataProperty>(rom.Metadatum.Age_Ratings
                    .Where(r => r.Split(':')[0] == preferredRatingsBoard)
                    .Select(r => new MetadataNameProperty(r)))
                : null;

            var links = new List<Link>();
            if (rom.SSId != null)
                links.Add(new Link("Screenscraper", $"https://www.screenscraper.fr/gameinfos.php?gameid={rom.SSId}"));
            if (rom.HasheousId != null)
                links.Add(new Link("Hasheous", $"https://hasheous.org/index.html?page=dataobjectdetail&type=game&id={rom.HasheousId}"));
            if (rom.RAId != null)
                links.Add(new Link("RetroAchievements", $"https://retroachievements.org/game/{rom.RAId}"));
            if (rom.SteamId != null)
                links.Add(new Link("Steam", $"https://store.steampowered.com/app/{rom.SteamId}"));
            if (rom.HLTBId != null)
                links.Add(new Link("HowLongToBeat", $"https://howlongtobeat.com/game/{rom.HLTBId}"));

            // Shown with the other links in the game's details (same file as the Manual field).
            if (rom.HasManual && !string.IsNullOrEmpty(rom.ManualPath))
                links.Add(new Link("Manual", $"{romMHost}/assets/romm/resources/{rom.ManualPath}"));

            return new GameMetadata
            {
                Name = rom.Name,
                Description = Description(romMHost, rom, imageAvailable),
                Regions = ToNameSet(rom.Regions),
                Genres = ToNameSet(rom.Metadatum.Genres),
                AgeRatings = ageRatings,
                // IGDB's collections are the series; franchises are wider (SeriesNames).
                Series = ToNameSet(SeriesNames.For(rom.Metadatum.Collections, rom.Metadatum.Franchises, rom.Name)),
                Features = ToNameSet(rom.Metadatum.Gamemodes),
                Categories = ToNameSet(rom.Metadatum.Collections),
                ReleaseDate = rom.Metadatum.Release_Date.HasValue
                    ? new ReleaseDate(UnixEpoch.AddMilliseconds(rom.Metadatum.Release_Date.Value).ToLocalTime())
                    : new ReleaseDate(),
                CommunityScore = (int?)rom.Metadatum.Average_Rating,
                CoverImage = !string.IsNullOrEmpty(rom.PathCoverL) ? new MetadataFile($"{romMHost}{rom.PathCoverL}") : null,
                // Game icon: the logo, else the cover; left blank when neither exists
                // so another metadata source can supply one.
                Icon = GameIcon(romMHost, rom),
                // Background: fan art, else a screenshot; left blank when neither exists so another
                // metadata source can supply one.
                BackgroundImage = GameBackground(romMHost, rom),
                LastActivity = rom.RomUser.LastPlayed,
                // RomM rating is 1-10, Playnite 1-100, so it can only be synced one direction without losing decimals.
                UserScore = rom.RomUser.Rating * 10,
                Links = links,
            };
        }

        // Steam's "About This Game" when the game is matched on Steam and the text is already at
        // hand (see SteamDescriptions; selecting the game fetches it otherwise), else RomM's summary.
        private static string Description(string romMHost, RomMRom rom, Func<string, bool> imageAvailable)
        {
            var steam = SteamDescriptions.Current?.Cached(rom.SteamId);
            string steamHtml = steam?.AboutHtmlForPlaynite;
            if (!string.IsNullOrWhiteSpace(steamHtml))
                return SteamDescriptions.Marker + DescriptionWithBoxArt(romMHost, rom, imageAvailable, steamHtml);
            return DescriptionWithBoxArt(romMHost, rom, imageAvailable);
        }

        // The summary (RomM's unless another is given), followed by the box front and back when
        // RomM has them. Playnite renders the description as HTML, so the art is appended as image
        // tags pointing at the server.
        internal static string DescriptionWithBoxArt(string romMHost, RomMRom rom, Func<string, bool> imageAvailable, string summary = null)
        {
            summary = summary ?? rom.Summary;
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(summary))
                parts.Add(summary);

            var ss = rom.SSMetadata;
            var images = new[]
                {
                    MediaUrl(romMHost, ss?.Box2dPath, ss?.Box2dUrl),
                    MediaUrl(romMHost, ss?.Box2dBackPath, ss?.Box2dBackUrl),
                }
                .Where(url => url != null && (imageAvailable == null || imageAvailable(url)))
                .Select(url => $"<img src=\"{System.Net.WebUtility.HtmlEncode(url)}\" style=\"width:49%\">")
                .ToList();
            // Each image takes just under half the description's width, with nothing between the
            // tags, so front and back sit side by side, run edge to edge and scale with the
            // window. A table was tried and dropped: Playnite's renderer lets a large image push
            // its column wider than its share, which clipped the row. A lone image keeps to its
            // half rather than being blown up to twice the size.
            if (images.Count > 0)
                parts.Add(string.Concat(images));

            // Nothing to add: hand back the summary exactly as RomM gave it (null included).
            if (parts.Count == 0 || (parts.Count == 1 && parts[0] == summary))
                return summary;

            return string.Join("<br><br>", parts);
        }

        // RomM's locally-cached file when there is one, else the external Screenscraper url, else null.
        private static string MediaUrl(string romMHost, string path, string url)
        {
            if (!string.IsNullOrEmpty(path))
                return $"{romMHost}/assets/romm/resources/{path}";
            return string.IsNullOrEmpty(url) ? null : url;
        }

        private static HashSet<MetadataProperty> ToNameSet(IEnumerable<string> values)
            => new HashSet<MetadataProperty>(values.Where(v => !string.IsNullOrEmpty(v)).Select(v => new MetadataNameProperty(v)));

        private static MetadataFile GameIcon(string romMHost, RomMRom rom)
        {
            string icon = IconUrl(romMHost, rom, out bool _);
            return string.IsNullOrEmpty(icon) ? null : new MetadataFile(icon);
        }

        /// <summary>
        /// Where the game's icon comes from: the logo (the same one saved for ExtraMetadataLoader),
        /// else the cover, else null so another metadata plugin can fill it. servedByRomM tells the
        /// caller whether the url is on the RomM host (send credentials) or external (never send them).
        /// </summary>
        public static string IconUrl(string romMHost, RomMRom rom, out bool servedByRomM)
        {
            string logo = LogoUrl(romMHost, rom, out servedByRomM);
            if (!string.IsNullOrEmpty(logo))
                return logo;

            // path_cover_large already carries RomM's resource mount, so it is host + path.
            servedByRomM = !string.IsNullOrEmpty(rom.PathCoverL);
            return servedByRomM ? $"{romMHost}{rom.PathCoverL}" : null;
        }

        /// <summary>
        /// Where to fetch the game's logo from, or null when RomM has none. servedByRomM tells the
        /// caller whether the url is on the RomM host (send credentials) or external (never send them).
        /// </summary>
        public static string LogoUrl(string romMHost, RomMRom rom, out bool servedByRomM)
        {
            servedByRomM = false;
            var ss = rom.SSMetadata;
            if (ss == null)
                return null;

            if (!string.IsNullOrEmpty(ss.LogoPath))
            {
                servedByRomM = true;
                return $"{romMHost}/assets/romm/resources/{ss.LogoPath}";
            }
            return string.IsNullOrEmpty(ss.LogoUrl) ? null : ss.LogoUrl;
        }

        private static MetadataFile GameBackground(string romMHost, RomMRom rom)
        {
            // Fan art first (RomM's cached file, then the external Screenscraper url), then a
            // screenshot in the same order.
            var ss = rom.SSMetadata;

            if (!string.IsNullOrEmpty(ss?.FanartPath))
                return new MetadataFile($"{romMHost}/assets/romm/resources/{ss.FanartPath}");
            if (!string.IsNullOrEmpty(ss?.FanartUrl))
                return new MetadataFile(ss.FanartUrl);

            string screenshot = rom.MergedScreenshots?.FirstOrDefault(s => !string.IsNullOrEmpty(s));
            if (!string.IsNullOrEmpty(screenshot))
                return new MetadataFile($"{romMHost}{screenshot}");
            if (!string.IsNullOrEmpty(ss?.ScreenshotUrl))
                return new MetadataFile(ss.ScreenshotUrl);
            return null;
        }
    }
}
