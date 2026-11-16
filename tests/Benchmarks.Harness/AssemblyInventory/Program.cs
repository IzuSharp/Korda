using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;

// Inspect compiled metadata without loading a target or initializing a native runtime.
foreach (string path in args)
{
    using FileStream stream = File.OpenRead(path);
    using PEReader file = new(stream);
    MetadataReader metadata = file.GetMetadataReader();
    List<string> exported = [];
    foreach (TypeDefinitionHandle handle in metadata.TypeDefinitions)
    {
        if (Visible(handle))
        {
            TypeDefinition type = metadata.GetTypeDefinition(handle);
            exported.Add(metadata.GetString(type.Namespace) + "." + metadata.GetString(type.Name));
        }
    }
    Console.WriteLine(JsonSerializer.Serialize(new { assembly = Path.GetFileName(path), publicApplicationTypes = exported }));
    if (exported.Count != 0)
    {
        return 1;
    }

    bool Visible(TypeDefinitionHandle handle)
    {
        TypeDefinition type = metadata.GetTypeDefinition(handle);
        TypeAttributes access = type.Attributes & TypeAttributes.VisibilityMask;
        return access == TypeAttributes.Public ||
            ((access is TypeAttributes.NestedPublic or TypeAttributes.NestedFamily or TypeAttributes.NestedFamORAssem)
             && Visible(type.GetDeclaringType()));
    }
}
return 0;
