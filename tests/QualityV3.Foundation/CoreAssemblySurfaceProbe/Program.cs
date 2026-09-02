using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

if (args.Length != 1)
{
    Console.Error.WriteLine("usage: QualityV3.CoreAssemblySurfaceProbe <immutable-artifact-directory>");
    return 2;
}

var artifactRoot = Path.GetFullPath(args[0]);
var contractsPath = Path.Combine(artifactRoot, "KnowledgeClipper.Core.V3.Contracts.dll");
var clientPath = Path.Combine(artifactRoot, "KnowledgeClipper.Core.V3.Client.dll");
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    var candidate = Path.Combine(artifactRoot, name.Name + ".dll");
    return File.Exists(candidate) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate) : null;
};
var contracts = AssemblyLoadContext.Default.LoadFromAssemblyPath(contractsPath);
var client = AssemblyLoadContext.Default.LoadFromAssemblyPath(clientPath);
var contractExports = contracts.GetExportedTypes().Select(type => type.FullName).OrderBy(value => value, StringComparer.Ordinal).ToArray();
var clientExports = client.GetExportedTypes();
var contractSignatureLeaks = clientExports
    .SelectMany(type => type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
    .SelectMany(MemberTypes)
    .Where(type => type is not null && type.Assembly == contracts)
    .Select(type => type!.FullName)
    .Distinct(StringComparer.Ordinal)
    .OrderBy(value => value, StringComparer.Ordinal)
    .ToArray();
var result = new
{
    schemaVersion = "quality-v3-core-assembly-surface-probe.v1",
    contractsExportedTypeCount = contractExports.Length,
    contractsExportedTypes = contractExports,
    clientExportedTypeCount = clientExports.Length,
    clientExportedTypes = clientExports.Select(type => type.FullName).OrderBy(value => value, StringComparer.Ordinal).ToArray(),
    clientPublicSignatureContractLeaks = contractSignatureLeaks
};
Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
return 0;

static IEnumerable<Type?> MemberTypes(MemberInfo member)
{
    if (member is MethodInfo method)
    {
        yield return Unwrap(method.ReturnType);
        foreach (var parameter in method.GetParameters()) yield return Unwrap(parameter.ParameterType);
    }
    else if (member is PropertyInfo property)
    {
        yield return Unwrap(property.PropertyType);
    }
    else if (member is FieldInfo field)
    {
        yield return Unwrap(field.FieldType);
    }
}

static Type Unwrap(Type type)
{
    while (type.IsGenericType && type.GetGenericArguments().Length == 1)
        type = type.GetGenericArguments()[0];
    return type;
}
