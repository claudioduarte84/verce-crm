using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Verce.Platform.Persistence;

/// <summary>Physical metadata contract for application-owned mutable/master tables only.</summary>
public static class ApplicationMetadataConfiguration
{
    public const string CreatedAt = nameof(CreatedAt);
    public const string CreatedBy = nameof(CreatedBy);
    public const string UpdatedAt = nameof(UpdatedAt);
    public const string UpdatedBy = nameof(UpdatedBy);

    public static EntityTypeBuilder<TEntity> HasApplicationMetadata<TEntity>(this EntityTypeBuilder<TEntity> builder)
        where TEntity : class
    {
        builder.Property<DateTimeOffset>(CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property<Guid?>(CreatedBy).HasColumnName("created_by");
        builder.Property<DateTimeOffset?>(UpdatedAt).HasColumnName("updated_at");
        builder.Property<Guid?>(UpdatedBy).HasColumnName("updated_by");
        return builder;
    }
}
