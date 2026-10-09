using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Classroom.Storage;
public sealed class DesignFactory : IDesignTimeDbContextFactory<ClassroomDb>
{
    public ClassroomDb CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<ClassroomDb>().UseSqlite("Data Source=classroom-design.sqlite").Options);
}
