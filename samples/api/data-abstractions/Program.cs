using Korda.Data;
using System.Runtime.CompilerServices;

if (args.Contains("--require-aot", StringComparer.Ordinal) && RuntimeFeature.IsDynamicCodeSupported)
    throw new InvalidOperationException("Actual Native AOT execution requires dynamic code to be unavailable.");
Console.WriteLine("Dynamic code supported: " + RuntimeFeature.IsDynamicCodeSupported);

// Ordinary C# business models and handwritten static mappings need no native runtime.
// B10 will generate equivalent calls; this executable does not claim that a generator ran.
var key = Students.Table.Key(Students.Id, 7L);
var changes = new Changes<Student>().Set(Students.Name, "Ada");
var parameters = new SqlParameters().Add("id", 7L, Codecs.Int64);
var command = SqlCommand.Create("SELECT Id, Name FROM Students WHERE Id = @id", parameters);
var projection = Projection<StudentSummary>.Create(
    [ResultField.Create("Id", Codecs.Int64), ResultField.Create("Name", Codecs.String)],
    static row => new StudentSummary(row.Read(0, Codecs.Int64), row.Read(1, Codecs.String)));
var relation = Relation<Student, Enrollment>.CreateOptional(Students.Table, Students.Id, Enrollments.Table, Enrollments.StudentId);

if (!Students.Table.TryGetColumn("Name", Codecs.String, out var name) || name != Students.Name)
    throw new InvalidOperationException("Exact-name descriptor lookup failed.");
if (Students.Table.TryGetColumn("name", Codecs.String, out _))
    throw new InvalidOperationException("A store name was silently changed.");

// The bounded mapping surface is ready for the downstream Database host. No query is executed here.
GC.KeepAlive(key); GC.KeepAlive(changes); GC.KeepAlive(command); GC.KeepAlive(relation);
Console.WriteLine("Korda.Data consumer passed: typed metadata, parameters, projection and relation; no native runtime.");
Console.WriteLine("Table: " + Students.Table.MetadataHash);
Console.WriteLine("Projection: " + projection.ShapeHash);

internal sealed record Student(long Id, string Name);
internal sealed record Enrollment(long Id, long? StudentId);
internal sealed record StudentSummary(long Id, string Name);
internal static class Students
{
    private static readonly TableBuilder<Student> Builder = new("Students");
    internal static readonly Column<Student, long> Id = Builder.Column("Id", Codecs.Int64, new() { IsPrimaryKey = true });
    internal static readonly Column<Student, string> Name = Builder.Column("Name", Codecs.String);
    internal static readonly Table<Student> Table = Builder.Build(
        static row => new(row.Get(Id), row.Get(Name)),
        static (row, value) => { row.Set(Id, value.Id); row.Set(Name, value.Name); });
}
internal static class Enrollments
{
    private static readonly TableBuilder<Enrollment> Builder = new("Enrollments");
    internal static readonly Column<Enrollment, long> Id = Builder.Column("Id", Codecs.Int64, new() { IsPrimaryKey = true });
    internal static readonly Column<Enrollment, long?> StudentId = Builder.Column("StudentId", Codecs.Nullable(Codecs.Int64));
    internal static readonly Table<Enrollment> Table = Builder.Build(
        static row => new(row.Get(Id), row.Get(StudentId)),
        static (row, value) => { row.Set(Id, value.Id); row.Set(StudentId, value.StudentId); });
}
