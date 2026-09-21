using System.Text.Json.Serialization;

namespace Verce.Modules.Quoting;

/// <summary>The seven revision states (STATE-MACHINES §1.1). <c>SUPERSEDED</c> is the addition
/// to the brief's six, required so "every revision is preserved" and "only the current revision
/// can be approved" hold simultaneously (ADR-0004).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<QuoteRevisionStatus>))]
public enum QuoteRevisionStatus { GENERATED, SENT, NEGOTIATING, APPROVED, CANCELED, EXPIRED, SUPERSEDED }

/// <summary>Quoting's own vocabulary for a fee's application (CR-07.3) — a snapshot VALUE copied
/// from Pricing's <c>FeeRuleVersion.FixedFeeApplication</c> by the composition root, never a
/// cross-module type reference (CLAUDE.md rule 11).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<QuoteFixedFeeApplication>))]
public enum QuoteFixedFeeApplication { PerUnit, PerOrder }

/// <summary>CR-08.2.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<QuoteDiscountKind>))]
public enum QuoteDiscountKind { None, Percent, Amount }

/// <summary>Business-history trigger (DOMAIN-MODEL §8 <c>QuoteStatusHistory</c>).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<QuoteHistoryTrigger>))]
public enum QuoteHistoryTrigger { USER, SYSTEM_JOB, EVENT }
