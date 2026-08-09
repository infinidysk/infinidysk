namespace NzbWebDAV.Database.MigrationHelpers.Attributes;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class ExecuteBeforeAttribute : Attribute
{
}
