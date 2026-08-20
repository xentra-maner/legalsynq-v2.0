using CareConnect.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CareConnect.Infrastructure.Data.Configurations;

public class SpecialtyConfiguration : IEntityTypeConfiguration<Specialty>
{
    public static readonly Guid PainId            = new("41000000-0000-0000-0000-000000000001");
    public static readonly Guid ChiropractorId    = new("41000000-0000-0000-0000-000000000002");
    public static readonly Guid OtherId            = new("41000000-0000-0000-0000-000000000010");
    public static readonly Guid PhysicalTherapyId  = new("41000000-0000-0000-0000-000000000004");
    public static readonly Guid ImagingId          = new("41000000-0000-0000-0000-000000000005");
    public static readonly Guid NeuroId           = new("41000000-0000-0000-0000-000000000006");
    public static readonly Guid SpineId            = new("41000000-0000-0000-0000-000000000007");

    public void Configure(EntityTypeBuilder<Specialty> builder)
    {
        builder.ToTable("cc_Specialties");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.Id).IsRequired();
        builder.Property(s => s.Name).IsRequired().HasMaxLength(200);
        builder.Property(s => s.Code).IsRequired().HasMaxLength(50);
        builder.Property(s => s.Description).HasMaxLength(1000);
        builder.Property(s => s.IsActive).IsRequired();
        builder.Property(s => s.CreatedAtUtc).IsRequired();
        builder.Property(s => s.UpdatedAtUtc).IsRequired();

        builder.HasIndex(s => s.Code).IsUnique();

        builder.HasMany(s => s.ProviderSpecialties)
            .WithOne(ps => ps.Specialty)
            .HasForeignKey(ps => ps.SpecialtyId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasData(
            CreateSpecialty(PainId, "Pain", "PAIN", null, true),
            CreateSpecialty(SpineId, "Spine", "SPINE", null, true),
            CreateSpecialty(PhysicalTherapyId, "Physical Therapy", "PHYSICAL_THERAPY", null, true),
            CreateSpecialty(NeuroId, "Neuro", "NEURO", null, true),
            CreateSpecialty(ImagingId, "Imaging", "IMAGING", null, true),
            CreateSpecialty(ChiropractorId, "Chiropractor", "CHIROPRACTOR", null, true),
            CreateSpecialty(OtherId, "Other", "OTHER", null, true)
        );
    }

    private static object CreateSpecialty(Guid id, string name, string code, string? description, bool isActive)
    {
        return new
        {
            Id = id,
            Name = name,
            Code = code,
            Description = description,
            IsActive = isActive,
            CreatedAtUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAtUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
    }
}
