namespace Tractus.HtmlToNdi.Models;

/// <summary>
/// The shape of the <c>/recipe</c> POST request body (D-06) — the analog of
/// <see cref="GoToUrlModel"/>. This DTO documents the request CONTRACT for OpenAPI/clients.
///
/// IMPORTANT (D-18): the <c>/recipe</c> endpoint (wired in Plan 04) does NOT bind directly to this
/// DTO via ASP.NET / System.Text.Json. Direct typed-DTO binding SILENTLY DROPS unknown fields, which
/// would defeat D-12's reject-unknown-fields requirement. Instead the endpoint reads the RAW request
/// body text and runs it through <c>RecipeValidator.TryNormalize(rawJson)</c> (which walks the raw
/// <c>JsonDocument</c> and rejects unknown keys) BEFORE mapping to the internal
/// <c>Recipe</c> model. This DTO therefore mirrors the <c>/seturl</c> endpoint's SHAPE but
/// deliberately NOT its direct-binding mechanism.
///
/// The field set is the locked D-02 flat schema (the spike's § Schema Impact left it unchanged):
/// <c>{urlMatch, css, js, targetSelector, fallbackPolicy, expectMotion}</c> + the optional
/// <c>expectsCrossOriginIframes</c> posture hint.
/// </summary>
public class RecipeDto
{
    public string? UrlMatch { get; set; }
    public string? Css { get; set; }
    public string? Js { get; set; }
    public string? TargetSelector { get; set; }
    public string? FallbackPolicy { get; set; }
    public bool? ExpectMotion { get; set; }
    public bool? ExpectsCrossOriginIframes { get; set; }
}
