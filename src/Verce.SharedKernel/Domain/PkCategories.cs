namespace Verce.SharedKernel.Domain;

// PK category markers (ADR-0011 §1). Every persisted, application-owned type implements
// EXACTLY ONE of these. Framework-owned types (Identity, Quartz, Data Protection) cannot
// implement these and are instead classified by Verce.Platform's technical registry.
// Verce.Architecture.Tests enforces "exactly one category" over the EF model.

/// <summary>
/// Category 1 — Domain entity: independent domain identity/lifecycle, created and managed
/// by the application. PK: uuid (UUID v7, generated in the application).
/// </summary>
public interface IDomainEntity
{
    Guid Id { get; }
}

/// <summary>
/// Category 2 — Master data: operator-managed business configuration/catalogue (the operator
/// creates, renames and retires rows at runtime). PK: uuid (UUID v7). Distinguished from
/// IDomainEntity by intent (who owns the row's lifecycle), not by PK shape.
/// </summary>
public interface IMasterData : IDomainEntity
{
}

/// <summary>
/// Category 3 — Reference data: a closed, system-owned enumeration represented relationally
/// because FKs/metadata require a table. The operator cannot create rows; the code IS the
/// semantic identity. PK: a stable textual code.
/// </summary>
public interface IReferenceData
{
    string Code { get; }
}

/// <summary>
/// Category 4 — Technical/framework table (application-owned): exists for infrastructure or
/// an algorithm, not domain identity (e.g. quote_number_counter, outbox_message). PK shape is
/// whatever the algorithm requires.
/// </summary>
public interface ITechnicalTable
{
}

/// <summary>
/// Category 5 — Join table: no independent lifecycle; identity IS the relationship. PK is a
/// composite of the two foreign keys. The model has none yet (ADR-0011 §1) — every apparent
/// many-to-many carries its own attributes and is therefore Category 1.
/// </summary>
public interface IJoinTable
{
}
