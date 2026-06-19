using System.Text;
using System.Text.Json;

namespace Tractus.HtmlToNdi.Chromium.Inject;

/// <summary>
/// The ONE shared, fail-closed recipe validator (D-12/D-18/D-19). It is used by BOTH the file-load
/// path (<see cref="RecipeStore"/>) and the Plan-04 <c>/recipe</c> HTTP body — a single code path so
/// the two surfaces cannot drift.
///
/// <para>D-18 (the load-bearing mechanism): validation parses the RAW JSON TEXT with
/// <see cref="JsonDocument"/> and walks the root object's properties against a known-field allowlist,
/// rejecting any unknown key. This is required because direct typed-DTO binding (ASP.NET /
/// System.Text.Json) SILENTLY DROPS unknown fields — so reject-unknown-fields is unachievable that
/// way. The raw-JSON walk catches them on BOTH the file and HTTP paths.</para>
///
/// <para>D-19 bounds: <c>css</c>/<c>js</c> must be non-empty WHEN PRESENT, within a per-field size cap,
/// and valid UTF-8. There is deliberately NO JS/CSS SYNTAX PARSING — runtime syntax failures are
/// caught by Plan 05's <c>--inject-smoke</c>, not here.</para>
///
/// <para>D-12: on ANY failure <see cref="TryNormalize"/> returns <c>(false, null, errors)</c> and
/// NEVER a partially-populated recipe.</para>
///
/// <para>SECURITY (V5 / threat T-01-04): the <c>urlMatch</c> glob is validated as DATA only; it is
/// never evaluated as control logic, and no page-derived string is treated as code.</para>
/// </summary>
public sealed class RecipeValidator
{
    /// <summary>
    /// D-19 per-field size cap for <c>css</c>/<c>js</c> payloads (Claude's Discretion). 256 KiB is far
    /// larger than any legitimate hide-chrome / fill-target inject payload (the spike payloads were a
    /// few KB) yet small enough to bound a malicious/oversized body well below a DoS-relevant size.
    /// </summary>
    public const int MaxFieldBytes = 256 * 1024;

    /// <summary>
    /// The known top-level recipe keys (D-18 allowlist). Matched case-insensitively. The locked D-02
    /// flat schema; the spike's § Schema Impact left it unchanged. Adding the future envelope keys
    /// here is the additive Phase-5 change (D-02).
    /// </summary>
    private static readonly HashSet<string> KnownKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "urlMatch",
        "css",
        "js",
        "targetSelector",
        "fallbackPolicy",
        "expectMotion",
        "expectsCrossOriginIframes",
    };

    /// <summary>
    /// Parse + validate + normalize a RAW recipe JSON string into the internal <see cref="Recipe"/>
    /// model (D-18). The SAME entry point serves file-load and the <c>/recipe</c> HTTP body.
    /// </summary>
    /// <param name="rawJson">The raw recipe JSON text (a file's contents or the POST body).</param>
    /// <returns>
    /// <c>(true, recipe, [])</c> on success; <c>(false, null, errors)</c> on ANY failure (never a
    /// partially-populated recipe, D-12). <paramref name="rawJson"/> is walked as a
    /// <see cref="JsonDocument"/> so unknown keys are rejected (D-18), not silently dropped.
    /// </returns>
    public (bool ok, Recipe? recipe, IReadOnlyList<string> errors) TryNormalize(string? rawJson)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(rawJson))
        {
            errors.Add("recipe JSON is empty");
            return (false, null, errors);
        }

        // D-19: valid UTF-8. A round-trip through the UTF-8 encoder/decoder with the replacement
        // fallback disabled throws on any invalid/unrepresentable sequence. We validate the bytes the
        // caller intends to persist/inject, not just that the string parsed.
        try
        {
            var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            var bytes = strict.GetBytes(rawJson);
            _ = strict.GetString(bytes);
        }
        catch (Exception ex) when (ex is EncoderFallbackException or DecoderFallbackException)
        {
            errors.Add("recipe JSON is not valid UTF-8");
            return (false, null, errors);
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(rawJson);
        }
        catch (JsonException ex)
        {
            errors.Add($"recipe JSON is malformed: {ex.Message}");
            return (false, null, errors);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                errors.Add("recipe JSON root must be an object");
                return (false, null, errors);
            }

            // D-18: walk the RAW properties and reject any key not in the allowlist. This is the step
            // a direct-DTO bind would skip — and the whole reason D-18 mandates the raw-JSON path.
            foreach (var prop in root.EnumerateObject())
            {
                if (!KnownKeys.Contains(prop.Name))
                {
                    errors.Add($"unknown recipe field '{prop.Name}'");
                }
            }

            var urlMatch = ReadString(root, "urlMatch", errors);
            var css = ReadString(root, "css", errors);
            var js = ReadString(root, "js", errors);
            var targetSelector = ReadString(root, "targetSelector", errors);
            var fallbackPolicy = ReadString(root, "fallbackPolicy", errors);
            var expectMotion = ReadBool(root, "expectMotion", errors);
            var expectsCrossOriginIframes = ReadBool(root, "expectsCrossOriginIframes", errors);

            // urlMatch: required, and a legal host + optional path glob (D-05). NOT a regex.
            if (string.IsNullOrWhiteSpace(urlMatch))
            {
                errors.Add("recipe field 'urlMatch' is required");
            }
            else if (!IsValidUrlMatch(urlMatch))
            {
                errors.Add($"recipe field 'urlMatch' is not a valid host + optional path glob: '{urlMatch}'");
            }

            // D-19: css/js non-empty WHEN PRESENT + within the per-field size cap. NO syntax parsing.
            ValidateOptionalPayload("css", css, errors);
            ValidateOptionalPayload("js", js, errors);

            if (errors.Count > 0)
            {
                // D-12: never a partially-populated recipe.
                return (false, null, errors);
            }

            var recipe = new Recipe
            {
                UrlMatch = urlMatch!,
                Css = css,
                Js = js,
                TargetSelector = targetSelector,
                FallbackPolicy = fallbackPolicy,
                ExpectMotion = expectMotion ?? false,
                ExpectsCrossOriginIframes = expectsCrossOriginIframes ?? false,
            };

            return (true, recipe, Array.Empty<string>());
        }
    }

    /// <summary>
    /// D-05 urlMatch validity: a host with an optional path glob. The host is checked structurally
    /// (no scheme, no spaces, at least one label, no <c>*</c> in the host portion); the path portion
    /// may contain the <c>*</c> wildcard (expanded to <c>.*</c> at match time in
    /// <see cref="RecipeStore"/>). This is a DATA validity check — the glob is never evaluated here.
    /// </summary>
    private static bool IsValidUrlMatch(string urlMatch)
    {
        var value = urlMatch.Trim();
        if (value.Length == 0)
        {
            return false;
        }

        // Reject an embedded scheme — urlMatch is a host(+path), not a URL.
        if (value.Contains("://", StringComparison.Ordinal))
        {
            return false;
        }

        // No whitespace anywhere.
        if (value.Any(char.IsWhiteSpace))
        {
            return false;
        }

        // Split host from the optional path glob at the first '/'.
        var slash = value.IndexOf('/');
        var host = slash < 0 ? value : value[..slash];
        var path = slash < 0 ? string.Empty : value[slash..];

        if (host.Length == 0)
        {
            return false;
        }

        // Host carries no glob wildcard (D-05: one file per host; globbing is path-only) and must look
        // like a dotted hostname: labels of [A-Za-z0-9-], at least one label, no empty labels.
        if (host.Contains('*', StringComparison.Ordinal))
        {
            return false;
        }

        var labels = host.Split('.');
        foreach (var label in labels)
        {
            if (label.Length == 0)
            {
                return false;
            }

            foreach (var c in label)
            {
                if (!(char.IsLetterOrDigit(c) || c == '-'))
                {
                    return false;
                }
            }
        }

        // Path (when present) may contain '*' but no other control characters; structural only.
        foreach (var c in path)
        {
            if (char.IsControl(c))
            {
                return false;
            }
        }

        return true;
    }

    private static void ValidateOptionalPayload(string field, string? value, List<string> errors)
    {
        if (value is null)
        {
            return; // absent is fine
        }

        if (value.Length == 0)
        {
            errors.Add($"recipe field '{field}' is present but empty");
            return;
        }

        var byteCount = Encoding.UTF8.GetByteCount(value);
        if (byteCount > MaxFieldBytes)
        {
            errors.Add($"recipe field '{field}' exceeds the {MaxFieldBytes}-byte size cap ({byteCount} bytes)");
        }
    }

    private static string? ReadString(JsonElement root, string name, List<string> errors)
    {
        if (!root.TryGetProperty(name, out var el))
        {
            return null;
        }

        switch (el.ValueKind)
        {
            case JsonValueKind.String:
                return el.GetString();
            case JsonValueKind.Null:
                return null;
            default:
                errors.Add($"recipe field '{name}' must be a string");
                return null;
        }
    }

    private static bool? ReadBool(JsonElement root, string name, List<string> errors)
    {
        if (!root.TryGetProperty(name, out var el))
        {
            return null;
        }

        switch (el.ValueKind)
        {
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Null:
                return null;
            default:
                errors.Add($"recipe field '{name}' must be a boolean");
                return null;
        }
    }
}
