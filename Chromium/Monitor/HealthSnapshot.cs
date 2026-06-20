using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tractus.HtmlToNdi.Chromium.Monitor;

/// <summary>
/// D-32: a kebab-lower <see cref="JsonNamingPolicy"/> so the string-serialized enum tokens match the
/// D-23/D-24 vocabulary the watchdog reads: <c>RecoveryExhausted</c> ⇒ <c>recovery-exhausted</c>,
/// <c>Healthy</c> ⇒ <c>healthy</c>, <c>Fallback</c> ⇒ <c>fallback</c>. .NET 8 has no built-in
/// <c>KebabCaseLower</c> policy nor <c>JsonStringEnumMemberName</c>, so this is hand-rolled and applied
/// through <see cref="KebabCaseStringEnumConverter"/> (an attribute-constructible
/// <see cref="JsonStringEnumConverter"/> subclass). PascalCase → kebab: lower the first char, and before
/// every subsequent uppercase char insert a hyphen then lower it.
/// </summary>
internal sealed class KebabCaseLowerNamingPolicy : JsonNamingPolicy
{
    public static readonly KebabCaseLowerNamingPolicy Instance = new();

    public override string ConvertName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        var sb = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0)
                {
                    sb.Append('-');
                }

                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}

/// <summary>
/// D-32 (load-bearing): an attribute-constructible <see cref="JsonStringEnumConverter"/> that serializes
/// enum values as their KEBAB-LOWER string tokens (never the System.Text.Json numeric default). Applied to
/// <see cref="MonitorStatus"/> and <see cref="FrameSource"/> so <c>/health</c> emits <c>"status":"recovering"</c>
/// / <c>"source":"fallback"</c> — the contract Phase 4's watchdog reads. .NET 8 has no generic
/// <c>JsonStringEnumConverter&lt;T&gt;</c>; this subclass passes the naming policy to the base ctor so the
/// attribute form <c>[JsonConverter(typeof(KebabCaseStringEnumConverter))]</c> carries the policy.
/// </summary>
internal sealed class KebabCaseStringEnumConverter : JsonStringEnumConverter
{
    public KebabCaseStringEnumConverter()
        : base(KebabCaseLowerNamingPolicy.Instance, allowIntegerValues: false)
    {
    }
}

/// <summary>
/// D-23/D-24: the rich, read-only <c>/health</c> contract — a self-describing liveness snapshot the
/// (deferred Phase-4) watchdog reads to decide restart-vs-leave-alone WITHOUT a re-instrumentation pass.
/// Every field already lives in the monitor / pump after Plans 02/04/05; <see cref="FrameMonitor.SnapshotHealth"/>
/// is a pure MAPPING method, not new instrumentation.
///
/// <para><b>D-24 freeze-vs-dead (load-bearing).</b> A running-but-frozen process answers HTTP 200 with this
/// snapshot — <see cref="Status"/> = <see cref="MonitorStatus.Tripped"/>/<see cref="MonitorStatus.Recovering"/>
/// and a HIGH <see cref="LastPaintAgeMs"/>. A DEAD process simply fails to connect (nothing to expose). So a
/// freeze NEVER looks like death; the watchdog infers death from the connection failure, never from a field.
/// There is deliberately NO "alive" boolean here that would make the two indistinguishable.</para>
///
/// <para><b>D-32 string enums (load-bearing).</b> <see cref="Status"/> and <see cref="Source"/> serialize as
/// their string tokens (e.g. <c>"recovering"</c>, <c>"recovery-exhausted"</c>, <c>"fallback"</c>) via
/// <see cref="JsonStringEnumConverter"/> applied to the enum types — NEVER the System.Text.Json numeric
/// default (which would emit <c>"status":3</c> and silently break the watchdog contract).</para>
///
/// <para><b>Information disclosure (T-2-06-1).</b> Exposes operational state ONLY — status, ages, counters,
/// the recipe <see cref="RecipeUrlMatch"/> summary, isolation posture. NO secrets, NO absolute filesystem
/// paths, no full sensitive URL beyond what <c>/recipe</c> already returns.</para>
/// </summary>
public sealed record HealthSnapshot
{
    /// <summary>The recovery state machine's current state (D-13). Serialized as a STRING token (D-32).</summary>
    [JsonPropertyName("status")]
    public required MonitorStatus Status { get; init; }

    /// <summary>Whether the on-air frame is the live render or the fallback hold (D-04). STRING token (D-32).</summary>
    [JsonPropertyName("source")]
    public required FrameSource Source { get; init; }

    /// <summary>
    /// Milliseconds since the last live paint was copied (D-24 — the freeze-vs-dead signal). A frozen
    /// renderer makes this climb high while the process still answers 200; a healthy live page keeps it low.
    /// </summary>
    [JsonPropertyName("lastPaintAgeMs")]
    public required double LastPaintAgeMs { get; init; }

    /// <summary>Total frames the single-authority pump has sent (the pump never stops, MON-03).</summary>
    [JsonPropertyName("framesSent")]
    public required long FramesSent { get; init; }

    /// <summary>Process uptime in seconds.</summary>
    [JsonPropertyName("uptimeSec")]
    public required double UptimeSec { get; init; }

    /// <summary>The current recipe's urlMatch summary (NO secrets / no full sensitive URL, T-2-06-1).</summary>
    [JsonPropertyName("recipe")]
    public string? RecipeUrlMatch { get; init; }

    /// <summary>The P1 startup site-isolation posture (rides the existing D-03 posture log — no re-instrumentation).</summary>
    [JsonPropertyName("isolationPosture")]
    public required string IsolationPosture { get; init; }

    /// <summary>Whether the fallback asset is the configured PNG or the generated default (D-20c). STRING token.</summary>
    [JsonPropertyName("fallbackAsset")]
    public required FallbackAssetState FallbackAsset { get; init; }

    /// <summary>Bounded refresh attempts issued in the current recovery episode (D-13).</summary>
    [JsonPropertyName("recoveryAttempts")]
    public required int RecoveryAttempts { get; init; }

    /// <summary>UTC timestamp of the most recent refresh issuance (the D-15 post-refresh recovery boundary).</summary>
    [JsonPropertyName("lastRefreshTs")]
    public required DateTime? LastRefreshTs { get; init; }

    /// <summary>Milliseconds since the last live copy (the cadence age the snapshot reports, D-15).</summary>
    [JsonPropertyName("lastGoodFrameAgeMs")]
    public required double LastGoodFrameAgeMs { get; init; }

    /// <summary>Which signal tripped the current fallback (cadence / freeze / blank reason); empty when live.</summary>
    [JsonPropertyName("fallbackReason")]
    public string? FallbackReason { get; init; }

    /// <summary>The most recent state transition + its triggering reason (Pitfall P-4).</summary>
    [JsonPropertyName("lastTransition")]
    public required TransitionInfo LastTransition { get; init; }

    /// <summary>The {ts, reason} shape of the most recent state transition.</summary>
    public sealed record TransitionInfo
    {
        /// <summary>UTC timestamp of the transition.</summary>
        [JsonPropertyName("ts")]
        public required DateTime Ts { get; init; }

        /// <summary>The reason string that triggered the transition.</summary>
        [JsonPropertyName("reason")]
        public required string Reason { get; init; }
    }
}

/// <summary>
/// D-04/D-23: the on-air frame source the <c>/health</c> snapshot reports. Serialized as a STRING token
/// (D-32) — <c>"live"</c> / <c>"fallback"</c> — via <see cref="JsonStringEnumConverter"/>.
/// </summary>
[JsonConverter(typeof(KebabCaseStringEnumConverter))]
public enum FrameSource
{
    /// <summary>The live render is on air (the normal path) — serialized as <c>"live"</c>.</summary>
    Live,

    /// <summary>The fallback hold frame is on air — serialized as <c>"fallback"</c>.</summary>
    Fallback,
}
