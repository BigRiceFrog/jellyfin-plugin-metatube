using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.MetaTube.Configuration;
using Jellyfin.Plugin.MetaTube.Extensions;
using Jellyfin.Plugin.MetaTube.Helpers;
using Jellyfin.Plugin.MetaTube.Metadata;
using Jellyfin.Plugin.MetaTube.Translation;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;
using MovieInfo = MediaBrowser.Controller.Providers.MovieInfo;
#if __EMBY__
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;

#else
using Jellyfin.Data.Enums;
using Microsoft.Extensions.Logging;
#endif

namespace Jellyfin.Plugin.MetaTube.Providers;

#if __EMBY__
public class MovieProvider : BaseProvider, IRemoteMetadataProvider<Movie, MovieInfo>, IHasOrder, IHasMetadataFeatures
#else
public class MovieProvider : BaseProvider, IRemoteMetadataProvider<Movie, MovieInfo>, IHasOrder
#endif
{
    private const string AvBase = "AVBASE";
    private const string Gfriends = "Gfriends";
    private const string Rating = "JP-18+";

    private static readonly string[] AvBaseSupportedProviderNames = { "DUGA", "FANZA", "Getchu", "MGS" };

#if __EMBY__
    public MetadataFeatures[] Features => new[]
        { MetadataFeatures.Collections, MetadataFeatures.Adult, MetadataFeatures.RequiredSetup };

    public MovieProvider(ILogManager logManager) : base(logManager.CreateLogger<MovieProvider>())
#else
    public MovieProvider(ILogger<MovieProvider> logger) : base(logger)
#endif
    {
    }

    public async Task<MetadataResult<Movie>> GetMetadata(MovieInfo info,
        CancellationToken cancellationToken)
    {
        var pid = info.GetPid(Plugin.ProviderId);
        if (string.IsNullOrWhiteSpace(pid.Id) || string.IsNullOrWhiteSpace(pid.Provider))
        {
            // Search movies and pick the result whose catalog number actually matches.
            // Walk every candidate query (original -> suffix-stripped -> leading number)
            // and accept the first hit whose catalog number matches, so a messy original
            // filename can no longer block scraping when a cleaner variant would match.
            var bestResult = await PickBestResultAsync(info, cancellationToken);
            if (bestResult != null) pid = bestResult.GetPid(Plugin.ProviderId);
        }

        // Nothing matched (e.g. a .strm file whose name carries no resolvable ID,
        // or a custom video with no catalog number). Skip gracefully instead of
        // calling the info API with empty provider/id and throwing a 404.
        if (string.IsNullOrWhiteSpace(pid.Id) || string.IsNullOrWhiteSpace(pid.Provider))
        {
            Logger.Warn("Movie not found, skip metadata: {0}", info.Name);
            return new MetadataResult<Movie> { HasMetadata = false };
        }

        Logger.Info("Get movie info: {0}", pid.ToString());

        var m = await ApiClient.GetMovieInfoAsync(pid.Provider, pid.Id, cancellationToken);

        // Preserve original title.
        var originalTitle = m.Title;

        // Convert to real actor names.
        if (Configuration.EnableRealActorNames)
            await ConvertToRealActorNames(m, cancellationToken);

        // Substitute title.
        if (Configuration.EnableTitleSubstitution)
            m.Title = Configuration.GetTitleSubstitutionTable().Substitute(m.Title);

        // Substitute actors.
        if (Configuration.EnableActorSubstitution)
            m.Actors = Configuration.GetActorSubstitutionTable().Substitute(m.Actors).ToArray();

        // Substitute genres.
        if (Configuration.EnableGenreSubstitution)
            m.Genres = Configuration.GetGenreSubstitutionTable().Substitute(m.Genres).ToArray();

        // Translate movie info.
        if (Configuration.TranslationMode != TranslationMode.Disabled)
            await TranslateMovieInfo(m, info.MetadataLanguage, cancellationToken);

        // Distinct and clean blank list
        m.Genres = m.Genres?.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray() ?? Array.Empty<string>();
        m.Actors = m.Actors?.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray() ?? Array.Empty<string>();
        m.PreviewImages = m.PreviewImages?.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray() ??
                          Array.Empty<string>();

        // Build parameters.
        var parameters = new Dictionary<string, string>
        {
            { @"{provider}", m.Provider },
            { @"{id}", m.Id },
            { @"{number}", m.Number },
            { @"{title}", m.Title },
            { @"{series}", m.Series },
            { @"{maker}", m.Maker },
            { @"{label}", m.Label },
            { @"{director}", m.Director },
            { @"{actors}", m.Actors?.Any() == true ? string.Join(' ', m.Actors) : string.Empty },
            { @"{first_actor}", m.Actors?.FirstOrDefault() },
            { @"{year}", $"{m.ReleaseDate:yyyy}" },
            { @"{month}", $"{m.ReleaseDate:MM}" },
            { @"{date}", $"{m.ReleaseDate:yyyy-MM-dd}" }
        };

        var result = new MetadataResult<Movie>
        {
            Item = new Movie
            {
                Name = RenderTemplate(
                    Configuration.EnableTemplate
                        ? Configuration.NameTemplate
                        : PluginConfiguration.DefaultNameTemplate, parameters),
                Tagline = RenderTemplate(
                    Configuration.EnableTemplate
                        ? Configuration.TaglineTemplate
                        : PluginConfiguration.DefaultTaglineTemplate, parameters),
                OriginalTitle = originalTitle,
                Overview = m.Summary,
                OfficialRating = Rating,
                PremiereDate = m.ReleaseDate.GetValidDateTime(),
                ProductionYear = m.ReleaseDate.GetValidYear(),
                Genres = m.Genres?.Any() == true ? m.Genres : Array.Empty<string>()
            },
            HasMetadata = true
        };

        // Set provider id.
        result.Item.SetPid(Name, m.Provider, m.Id, pid.Position);

        // Set trailer url.
        var trailerUrl = !string.IsNullOrWhiteSpace(m.PreviewVideoUrl)
            ? m.PreviewVideoUrl
            : m.PreviewVideoHlsUrl;
        if (!string.IsNullOrWhiteSpace(trailerUrl))
            result.Item.SetTrailerUrl(trailerUrl);

        // Set community rating.
        if (Configuration.EnableRatings)
            result.Item.CommunityRating = m.Score > 0 ? (float)Math.Round(m.Score * 2, 1) : null;

        // Add collection.
        if (Configuration.EnableCollections && !string.IsNullOrWhiteSpace(m.Series))
        {
            result.Item.AddCollection(m.Series);
            Logger.Info("Add Collection for movie {0} [{1}]", pid.ToString(), m.Series);
        }

        // Add studio.
        if (!string.IsNullOrWhiteSpace(m.Maker))
            result.Item.AddStudio(m.Maker);

        // Add tag (series).
        if (!string.IsNullOrWhiteSpace(m.Series))
            result.Item.AddTag(m.Series);

        // Add tag (maker).
        if (!string.IsNullOrWhiteSpace(m.Maker))
            result.Item.AddTag(m.Maker);

        // Add tag (label).
        if (!string.IsNullOrWhiteSpace(m.Label))
            result.Item.AddTag(m.Label);

        // Add director.
        if (Configuration.EnableDirectors && !string.IsNullOrWhiteSpace(m.Director))
            result.AddPerson(new PersonInfo
            {
                Name = m.Director,
#if __EMBY__
                Type = PersonType.Director
#else
                Type = PersonKind.Director
#endif
            });

        // Add actors.
        foreach (var name in m.Actors ?? Enumerable.Empty<string>())
        {
            var actor = new PersonInfo
            {
                Name = name,
#if __EMBY__
                Type = PersonType.Actor,
#else
                Type = PersonKind.Actor,
#endif
            };
            await SetActorImageUrl(actor, cancellationToken);
            result.AddPerson(actor);
        }

        return result;
    }

    public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(MovieInfo info,
        CancellationToken cancellationToken)
    {
        var pid = info.GetPid(Plugin.ProviderId);

        var searchResults = new List<MovieSearchResult>();
        if (string.IsNullOrWhiteSpace(pid.Id) || string.IsNullOrWhiteSpace(pid.Provider))
        {
            // Search movie by name. Try the original name first, then progressively
            // normalized variants (strip uncensored/variant suffixes and Chinese tokens,
            // finally the leading catalog number) so that .strm files with messy names
            // like "OFJE-550-D" or "FSDSS-789_深田えいみ_无码破解" can still match.
            // NOTE: accumulate every candidate's hits (no early break) so the Identify
            // dialog shows the broadest possible set to choose from.
            foreach (var query in GetSearchCandidates(info.Name))
            {
                Logger.Info("Search for movie: {0}", query);
                try
                {
                    var matched = await ApiClient.SearchMovieAsync(query, pid.Provider, cancellationToken);
                    if (matched != null && matched.Any())
                        searchResults.AddRange(matched);
                }
                catch (Exception e)
                {
                    Logger.Warn("Search failed for movie: {0} ({1})", query, e.Message);
                }
            }
        }
        else
        {
            // Exact search.
            Logger.Info("Search for movie: {0}", pid.ToString());
            searchResults.Add(await ApiClient.GetMovieInfoAsync(pid.Provider, pid.Id,
                pid.Update != true, cancellationToken));
        }

        if (Configuration.EnableMovieProviderFilter)
        {
            if (Configuration.GetMovieProviderFilter() is { } filter &&
                filter.Any()) // Apply only if filter is not empty.
            {
                // Filter out mismatched results.
                searchResults.RemoveAll(m => !filter.Contains(m.Provider, StringComparer.OrdinalIgnoreCase));
                // Reorder results by stable sort.
                searchResults = searchResults.OrderBy(m =>
                    filter.FindIndex(s => s.Equals(m.Provider, StringComparison.OrdinalIgnoreCase))).ToList();
            }
            else
            {
                Logger.Warn("Movie provider filter enabled but never used");
            }
        }

        var results = new List<RemoteSearchResult>();
        if (!searchResults.Any())
        {
            Logger.Warn("Movie not found or has been filtered: {0}", pid.Id);
            return results;
        }

        foreach (var m in searchResults)
            results.Add(ToRemoteSearchResult(m, pid.Position));

        return results;
    }

    /// <summary>
    /// Walks the candidate queries (original name -> suffix-stripped -> leading catalog
    /// number) and returns the first search result whose catalog number actually matches
    /// the query. Unlike a single blind search, this keeps trying cleaner variants after a
    /// fuzzy/wrong first hit, which is what recovers scraping for .strm files whose messy
    /// filename yields a non-matching result on the first attempt. Wrong images are still
    /// avoided because every accepted result must pass the catalog-number check.
    /// </summary>
    private async Task<RemoteSearchResult> PickBestResultAsync(MovieInfo info,
        CancellationToken cancellationToken)
    {
        var pid = info.GetPid(Plugin.ProviderId);

        foreach (var query in GetSearchCandidates(info.Name))
        {
            Logger.Info("Search for movie: {0}", query);
            List<MovieSearchResult> matched;
            try
            {
                matched = (await ApiClient.SearchMovieAsync(query, pid.Provider, cancellationToken))?.ToList();
            }
            catch (Exception e)
            {
                Logger.Warn("Search failed for movie: {0} ({1})", query, e.Message);
                continue;
            }

            if (matched == null || !matched.Any()) continue;

            // Apply the provider filter the same way GetSearchResults does.
            if (Configuration.EnableMovieProviderFilter &&
                Configuration.GetMovieProviderFilter() is { } filter && filter.Any())
            {
                matched.RemoveAll(m => !filter.Contains(m.Provider, StringComparer.OrdinalIgnoreCase));
            }

            var remote = matched.Select(m => ToRemoteSearchResult(m, pid.Position)).ToList();
            var best = PickBestResult(remote, query);
            if (best != null) return best;

            Logger.Warn("Candidate \"{0}\" returned results but none matched the catalog number, trying next variant",
                query);
        }

        Logger.Warn("No search result matched any catalog-number variant for \"{0}\", skip to avoid wrong metadata/image",
            info.Name);
        return null;
    }

    private RemoteSearchResult ToRemoteSearchResult(MovieSearchResult m, double? position)
    {
        var result = new RemoteSearchResult
        {
            Name = $"[{m.Provider}] {m.Number} {m.Title}",
            SearchProviderName = Name,
            PremiereDate = m.ReleaseDate.GetValidDateTime(),
            ProductionYear = m.ReleaseDate.GetValidYear(),
            ImageUrl = ApiClient.GetPrimaryImageApiUrl(m.Provider, m.Id, m.ThumbUrl, 1.0, true)
        };
        result.SetPid(Name, m.Provider, m.Id, position);
        return result;
    }

    /// <summary>
    /// Extracts the leading catalog number for comparison, e.g. "SSIS-462-UC" -> "SSIS462".
    /// </summary>
    private static string ExtractCatalogNumber(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var m = Regex.Match(name, @"[A-Za-z]{2,8}[-_]?\d{2,6}");
        if (!m.Success) return null;
        return Regex.Replace(m.Value.ToUpperInvariant(), "[^A-Z0-9]", string.Empty);
    }

    /// <summary>
    /// Picks the search result whose CORE catalog number exactly equals the query's core
    /// catalog number. Taking the first result blindly, or using loose prefix matching,
    /// can bind an unrelated movie to the file, producing wrong metadata AND a wrong cover
    /// image (番号对不上). The core is the leading letters + digits with any variant suffix
    /// (e.g. -UC, -C, -4K, HE) ignored, so "SSIS-462-C" still matches "SSIS-462" while
    /// "SSIS-462" can never accidentally match "SSIS-4620".
    /// </summary>
    private RemoteSearchResult PickBestResult(IEnumerable<RemoteSearchResult> results, string query)
    {
        var list = results?.Where(r => r != null).ToList() ?? new List<RemoteSearchResult>();
        if (list.Count == 0) return null;

        var expected = ExtractCatalogNumber(query);
        // No resolvable catalog number in the query -> cannot verify, fall back to first.
        if (string.IsNullOrEmpty(expected)) return list[0];

        foreach (var r in list)
        {
            // Search result name is formatted as "[Provider] NUMBER Title".
            var m = Regex.Match(r.Name ?? string.Empty, @"^\[[^\]]+\]\s*(\S+)");
            if (!m.Success) continue;
            var actual = ExtractCatalogNumber(m.Groups[1].Value);
            if (string.IsNullOrEmpty(actual)) continue;
            if (actual == expected) return r;
        }

        Logger.Warn("No search result matches catalog number {0} for \"{1}\", skip to avoid wrong metadata/image",
            expected, query);
        return null;
    }

    private static IEnumerable<string> GetSearchCandidates(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) yield break;

        // Original query first - never degrade a working match.
        yield return name;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { name };

        // Strip trailing uncensored / variant markers and Chinese suffix tokens.
        var s = Regex.Replace(name,
            @"[_\-]?(UC|UCS|CR|CRB|UCF|C|F|D|B|U|无码破解|无码|破解|流出|无修正|中文字幕|中文|无码流出)$",
            string.Empty, RegexOptions.IgnoreCase);
        if (seen.Add(s)) yield return s;

        // Fall back to the leading catalog number (e.g. FSDSS-789, SSNI-591, ABP001).
        var m = Regex.Match(name, @"^[A-Za-z]{2,8}[-]?\d{2,6}", RegexOptions.IgnoreCase);
        if (m.Success && seen.Add(m.Value)) yield return m.Value;
    }

    private async Task SetActorImageUrl(PersonInfo actor, CancellationToken cancellationToken)
    {
        try
        {
            var results = await ApiClient.SearchActorAsync(actor.Name, cancellationToken);
            if (results?.Any() != true)
            {
                Logger.Warn("Actor not found: {0}", actor.Name);
                return;
            }

            // Use the first result as the primary actor selection.
            var firstResult = results.First();
            if (firstResult.Images?.Any() == true)
            {
                actor.ImageUrl = ApiClient.GetPrimaryImageApiUrl(
                    firstResult.Provider, firstResult.Id, firstResult.Images.First(), 0.5, true);
                actor.SetPid(Name, firstResult.Provider, firstResult.Id);
            }

            // Use the Gfriends to update the actor profile image, if any.
            foreach (var result in results.Where(result => result.Provider == Gfriends && result.Images?.Any() == true))
            {
                actor.ImageUrl = ApiClient.GetPrimaryImageApiUrl(
                    result.Provider, result.Id, result.Images.First(), 0.5, true);
            }
        }
        catch (Exception e)
        {
            Logger.Error("Get actor image error: {0} ({1})", actor.Name, e.Message);
        }
    }

    private async Task ConvertToRealActorNames(MovieSearchResult m, CancellationToken cancellationToken)
    {
        if (!AvBaseSupportedProviderNames.Contains(m.Provider, StringComparer.OrdinalIgnoreCase)) return;

        try
        {
            var searchResults = await ApiClient.SearchMovieAsync(m.Id, AvBase, cancellationToken);
            if (searchResults?.Any() != true)
            {
                Logger.Warn("Movie not found on AVBASE: {0}", m.Id);
                return;
            }

            foreach (var result in searchResults)
            {
                var similarity = CalculateTitleSimilarity(m, result);

                Logger.Info("Calculate movie title similarity for {0} ({1}) and {2} ({3}): {4:0.00%}",
                    m.Id, m.Provider, result.Id, result.Provider, similarity);

                if (similarity >= 0.8)
                {
                    if (result.Actors?.Any() == true)
                        m.Actors = result.Actors;
                    return;
                }
            }

            Logger.Warn("No matching movie found on AVBASE for {0}", m.Id);
        }
        catch (Exception e)
        {
            Logger.Error("Convert to real actor names error: {0} ({1})", m.Number, e.Message);
        }
    }

    private static double CalculateTitleSimilarity(MovieSearchResult source, MovieSearchResult target)
    {
        var sourceKey = Normalize(source.Number + source.Title);
        var targetKey = Normalize(target.Number + target.Title);

        if (string.IsNullOrWhiteSpace(sourceKey) || string.IsNullOrWhiteSpace(targetKey))
            return 0.0;

        var distance = Levenshtein.Distance(sourceKey, targetKey);
        var avgLength = (sourceKey.Length + targetKey.Length) / 2.0;
        var similarity = 1.0 - distance / avgLength;

        return Math.Clamp(similarity, 0.0, 1.0);

        string Normalize(string s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return string.Empty;

            s = s.ToLowerInvariant();
            s = Regex.Replace(s, @"[\s\[\]\(\)【】（）]", "");
            return s.Trim();
        }
    }

    private async Task TranslateMovieInfo(Metadata.MovieInfo m, string language, CancellationToken cancellationToken)
    {
        try
        {
            Logger.Info("Translate movie info language: {0} => {1}", m.Number, language);
            await TranslationHelper.TranslateAsync(m, language, cancellationToken);
        }
        catch (Exception e)
        {
            Logger.Error("Translate error: {0}", e.Message);
        }
    }

    private static string RenderTemplate(string template, Dictionary<string, string> parameters)
    {
        if (string.IsNullOrWhiteSpace(template))
            return string.Empty;

        var sb = parameters.Where(kvp => template.Contains(kvp.Key))
            .Aggregate(new StringBuilder(template),
                (sb, kvp) => sb.Replace(kvp.Key, kvp.Value));

        return sb.ToString().Trim();
    }
}