using Microsoft.EntityFrameworkCore;

namespace Classroom.Storage;
public sealed class GroupEntity
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class ProfileEntity
{
    public string Id { get; set; } = "";
    public string Login { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = "student";
    public string? GroupId { get; set; }
    public int Slot { get; set; }
    public long LockedUntil { get; set; }
    public string ThemeJson { get; set; } = "{\"preset\":\"light\"}";
    public string PresetsJson { get; set; } = "[]";
}

public sealed class TaskEntity
{
    public string Id { get; set; } = "";
    public string Json { get; set; } = "";
    public int Revision { get; set; } = 1;
    public bool Deleted { get; set; }
    public bool Restricted { get; set; }
}

public sealed class TaskRevisionEntity
{
    public string TaskId { get; set; } = "";
    public int Revision { get; set; }
    public string Json { get; set; } = "";
    public string AlternativesJson { get; set; } = "[]";
    public bool Restricted { get; set; }
    public bool Deleted { get; set; }
    public long At { get; set; }
}

public sealed class VariantEntity
{
    public string TaskId { get; set; } = "";
    public int Index { get; set; }
    public string Json { get; set; } = "";
}

public sealed class AssignmentEntity
{
    public string StudentId { get; set; } = "";
    public string TaskId { get; set; } = "";
    public string Json { get; set; } = "";
    public int Revision { get; set; } = 1;
    public long? CompletedAt { get; set; }
    public string HintsJson { get; set; } = "[]";
    public string Draft { get; set; } = "";
    public int ProjectOrder { get; set; }
    public int ActiveSeconds { get; set; }
    public int Checks { get; set; }
    public int Errors { get; set; }
}

public sealed class SnapshotEntity
{
    public string StudentId { get; set; } = "";
    public string Project { get; set; } = "";
    public string Base64 { get; set; } = "";
}

public sealed class AttemptEntity
{
    public string Id { get; set; } = "";
    public string StudentId { get; set; } = "";
    public string TaskId { get; set; } = "";
    public string Kind { get; set; } = "";
    public long At { get; set; }
}

public sealed class OperationEntity
{
    public string StudentId { get; set; } = "";
    public string Id { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public string ResponseJson { get; set; } = "";
    public long At { get; set; }
}

public sealed class ImportEntity
{
    public string Hash { get; set; } = "";
    public long At { get; set; }
}

public sealed class ClassroomDb(DbContextOptions<ClassroomDb> options) : DbContext(options)
{
    public DbSet<GroupEntity> Groups => Set<GroupEntity>();
    public DbSet<ProfileEntity> Profiles => Set<ProfileEntity>();
    public DbSet<TaskEntity> Tasks => Set<TaskEntity>();
    public DbSet<TaskRevisionEntity> TaskRevisions => Set<TaskRevisionEntity>();
    public DbSet<VariantEntity> Variants => Set<VariantEntity>();
    public DbSet<AssignmentEntity> Assignments => Set<AssignmentEntity>();
    public DbSet<SnapshotEntity> Snapshots => Set<SnapshotEntity>();
    public DbSet<AttemptEntity> Attempts => Set<AttemptEntity>();
    public DbSet<OperationEntity> Operations => Set<OperationEntity>();
    public DbSet<ImportEntity> Imports => Set<ImportEntity>();

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        foreach (var entry in ChangeTracker.Entries<TaskEntity>().Where(e => e.State is EntityState.Added or EntityState.Modified).ToArray())
        {
            var task = entry.Entity;
            if (await TaskRevisions.AnyAsync(r => r.TaskId == task.Id && r.Revision == task.Revision, cancellationToken))
                task.Revision++;
            var alternatives = await Variants.Where(v => v.TaskId == task.Id).ToListAsync(cancellationToken);
            foreach (var local in Variants.Local.Where(v => v.TaskId == task.Id))
            {
                alternatives.RemoveAll(v => v.Index == local.Index);
                alternatives.Add(local);
            }

            var definitions = Classroom.Domain.Variants.Create(Classroom.Domain.Catalog.Read(task.Json));
            foreach (var variant in alternatives)
                if (variant.Index >= 2 && variant.Index - 2 < definitions.Length)
                    definitions[variant.Index - 2] = Classroom.Domain.Catalog.Read(variant.Json);
            TaskRevisions.Add(new() { TaskId = task.Id, Revision = task.Revision, Json = task.Json, AlternativesJson = Classroom.Contracts.Wire.Write(definitions.Select(Classroom.Domain.Catalog.Write).ToArray()), Restricted = task.Restricted, Deleted = task.Deleted, At = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
        }

        return await base.SaveChangesAsync(cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder model) => Configure(model);
    public static void Configure(ModelBuilder model)
    {
        model.Entity<GroupEntity>().HasKey(x => x.Id);
        model.Entity<ProfileEntity>().HasKey(x => x.Id);
        model.Entity<ProfileEntity>().HasIndex(x => x.Login).IsUnique();
        model.Entity<ProfileEntity>().HasOne<GroupEntity>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<TaskEntity>().HasKey(x => x.Id);
        model.Entity<TaskRevisionEntity>().HasKey(x => new { x.TaskId, x.Revision });
        model.Entity<TaskRevisionEntity>().HasOne<TaskEntity>().WithMany().HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<VariantEntity>().HasKey(x => new { x.TaskId, x.Index });
        model.Entity<VariantEntity>().HasOne<TaskEntity>().WithMany().HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<AssignmentEntity>().HasKey(x => new { x.StudentId, x.TaskId });
        model.Entity<AssignmentEntity>().HasOne<ProfileEntity>().WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<SnapshotEntity>().HasKey(x => new { x.StudentId, x.Project });
        model.Entity<SnapshotEntity>().HasOne<ProfileEntity>().WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<AttemptEntity>().HasKey(x => x.Id);
        model.Entity<AttemptEntity>().HasOne<ProfileEntity>().WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<OperationEntity>().HasKey(x => new { x.StudentId, x.Id });
        model.Entity<OperationEntity>().HasOne<ProfileEntity>().WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<ImportEntity>().HasKey(x => x.Hash);
    }
}
