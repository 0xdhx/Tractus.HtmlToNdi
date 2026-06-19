using System.Text.RegularExpressions;
using Serilog;

namespace Tractus.HtmlToNdi.Chromium.Inject;

/// <summary>
/// Loads recipe files from a directory, validates each through the shared <see cref="RecipeValidator"/>
/// (D-12/D-18 validate-on-load), and resolves a URL to a recipe via host + optional path-glob,
/// first-match-wins (D-05).
///
/// <para>D-20 — the THREE failure modes are DISTINCT, separable surfaces (so Plan-04 startup wiring can
/// fail-loud on the explicit path while staying resilient on the dir scan):</para>
/// <list type="number">
///   <item>A dir-scan file that fails parse/validate is SKIPPED + logged (<see cref="Load"/>) — an
///   unrelated malformed file never crashes startup (resilience).</item>
///   <item>An EXPLICIT <c>--recipe &lt;name&gt;</c> lookup that fails is a HARD failure result
///   (<see cref="TryLoadExplicit"/>) the caller can fail-loud on — it is NOT silently skipped.</item>
///   <item>A <see cref="Match"/> miss returns <c>null</c> + the exact <c>"no recipe for &lt;host&gt;"</c>
///   pass-through warning (D-07) — the page renders unmodified.</item>
/// </list>
///
/// <para>D-21: <see cref="PostureMatches"/> reports whether a candidate recipe's
/// <see cref="Recipe.ExpectsCrossOriginIframes"/> matches a launch posture captured at startup
/// (site-isolation flags are frozen at <c>Cef.Initialize</c>), so Plan 04's swap can reject a
/// posture-mismatched swap with a structured error.</para>
///
/// <para>SECURITY (V5 / threat T-01-04): the urlMatch glob is matched via a <c>*</c>→<c>.*</c>
/// expansion, NEVER evaluated; the glob is internal (D-05), not a user-supplied regex.</para>
/// </summary>
public sealed class RecipeStore
{
    private readonly RecipeValidator validator;

    // host (lower-case) → recipe. One recipe per host (D-05). Insertion order is preserved for
    // first-match-wins among path globs on the same host.
    private readonly List<Recipe> recipes = new();

    public RecipeStore(RecipeValidator validator)
    {
        this.validator = validator ?? throw new ArgumentNullException(nameof(validator));
    }

    /// <summary>The recipes currently loaded into the store (read-only view).</summary>
    public IReadOnlyList<Recipe> Recipes => this.recipes;

    /// <summary>
    /// D-20 (mode 1): scan <paramref name="dir"/> for every <c>*.json</c>, validate each through the
    /// shared validator, and index the valid ones. A malformed/invalid file is SKIPPED + logged at
    /// Warning — it NEVER crashes startup. A missing or empty directory loads cleanly (empty store).
    /// </summary>
    public void Load(string dir)
    {
        this.recipes.Clear();

        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            Log.Warning("recipe directory not found, loading no recipes: {Dir}", dir);
            return;
        }

        foreach (var file in Directory.EnumerateFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            string rawJson;
            try
            {
                rawJson = File.ReadAllText(file);
            }
            catch (Exception ex)
            {
                // D-20 (mode 1): a read error on an unrelated file is skipped + warned, not fatal.
                Log.Warning("skipping unreadable recipe file {File}: {Message}", file, ex.Message);
                continue;
            }

            var (ok, recipe, errors) = this.validator.TryNormalize(rawJson);
            if (!ok || recipe is null)
            {
                // D-20 (mode 1): dir-scan malformed file → skip + warn, never crash.
                Log.Warning("skipping malformed recipe file {File}: {Errors}", file, string.Join("; ", errors));
                continue;
            }

            this.recipes.Add(recipe);
        }

        Log.Information("loaded {Count} recipe(s) from {Dir}", this.recipes.Count, dir);
    }

    /// <summary>
    /// D-20 (mode 2): the EXPLICIT-lookup surface. Reads a single named recipe file and returns a HARD
    /// failure result on read/parse/validate failure (NOT a silent skip) so Plan 04's <c>--recipe</c>
    /// startup wiring can fail-loud (non-zero exit / Log.Error + return). On success the recipe is also
    /// indexed into the store.
    /// </summary>
    /// <param name="path">The explicit recipe file path (from <c>--recipe &lt;name&gt;</c>).</param>
    /// <param name="recipe">The normalized recipe on success; <c>null</c> on failure.</param>
    /// <param name="error">A structured error message on failure; <c>null</c> on success.</param>
    /// <returns><c>true</c> if the explicit recipe loaded + validated; <c>false</c> (hard failure) otherwise.</returns>
    public bool TryLoadExplicit(string path, out Recipe? recipe, out string? error)
    {
        recipe = null;
        error = null;

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            error = $"explicit recipe file not found: {path}";
            return false;
        }

        string rawJson;
        try
        {
            rawJson = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            error = $"explicit recipe file unreadable: {path}: {ex.Message}";
            return false;
        }

        var (ok, normalized, errors) = this.validator.TryNormalize(rawJson);
        if (!ok || normalized is null)
        {
            // D-20 (mode 2): explicit recipe failure is HARD — surfaced to the caller, not skipped.
            error = $"explicit recipe '{path}' is invalid: {string.Join("; ", errors)}";
            return false;
        }

        this.recipes.Add(normalized);
        recipe = normalized;
        return true;
    }

    /// <summary>
    /// Resolve <paramref name="url"/> to a recipe: extract the host, find the first recipe whose
    /// urlMatch host (+ optional path glob) matches, first-match-wins (D-05). On no match, emit the
    /// exact <c>"no recipe for &lt;host&gt;"</c> warning and return <c>null</c> so the caller injects
    /// nothing (D-07 pass-through; the page renders unmodified).
    /// </summary>
    public Recipe? Match(string url)
    {
        string host;
        try
        {
            host = new Uri(url).Host;
        }
        catch (Exception)
        {
            Log.Warning("no recipe for {Host}", url);
            return null;
        }

        var path = string.Empty;
        try
        {
            path = new Uri(url).AbsolutePath;
        }
        catch (Exception)
        {
            // host already parsed above; treat path as empty.
        }

        foreach (var recipe in this.recipes)
        {
            if (UrlMatchMatches(recipe.UrlMatch, host, path))
            {
                return recipe; // first-match-wins (D-05)
            }
        }

        // D-07 pass-through: exact warning string "no recipe for <host>".
        Log.Warning("no recipe for {Host}", host);
        return null;
    }

    /// <summary>
    /// D-21 posture-compatibility check. Returns whether a candidate recipe may be swapped in under the
    /// FROZEN launch posture (site-isolation flags fixed at <c>Cef.Initialize</c>). A swap is only
    /// allowed when the candidate's <see cref="Recipe.ExpectsCrossOriginIframes"/> equals the launch
    /// posture; Plan 04's swap path rejects a mismatch with a structured error directing the operator
    /// to relaunch (the seamless auto-restart variant is DEFERRED — do not build it).
    /// </summary>
    /// <param name="launchExpectsCrossOriginIframes">The posture captured at startup.</param>
    /// <param name="candidate">The recipe a swap would apply.</param>
    public static bool PostureMatches(bool launchExpectsCrossOriginIframes, Recipe candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return launchExpectsCrossOriginIframes == candidate.ExpectsCrossOriginIframes;
    }

    /// <summary>
    /// Match a urlMatch pattern (host + optional path glob) against a concrete host + path. The host
    /// must match case-insensitively and exactly (D-05: one file per host). The optional path glob is
    /// matched via a tiny <c>*</c>→<c>.*</c> expansion compiled with
    /// <see cref="System.Text.RegularExpressions"/> — an INTERNAL glob, never user-supplied regex.
    /// </summary>
    private static bool UrlMatchMatches(string urlMatch, string host, string path)
    {
        var pattern = urlMatch.Trim();
        var slash = pattern.IndexOf('/');
        var patternHost = slash < 0 ? pattern : pattern[..slash];
        var patternPath = slash < 0 ? string.Empty : pattern[slash..];

        if (!string.Equals(patternHost, host, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (patternPath.Length == 0)
        {
            return true; // host-only pattern matches any path
        }

        // Expand the internal glob: escape regex metacharacters, then turn the literal '*' (now the
        // escaped sequence) back into '.*'. The glob is matched, never executed (V5).
        var regex = "^" + Regex.Escape(patternPath).Replace("\\*", ".*") + "$";
        return Regex.IsMatch(path, regex, RegexOptions.IgnoreCase);
    }
}
