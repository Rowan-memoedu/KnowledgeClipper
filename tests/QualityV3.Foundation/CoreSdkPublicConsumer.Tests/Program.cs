using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using KnowledgeClipper.Core.V3.Client;

var clientAssembly = typeof(CoreV3Client).Assembly;
var protocolClient = clientAssembly.GetType("KnowledgeClipper.Core.V3.Client.CoreV3ProtocolClient", throwOnError: true)!;
var facadeContractLeaks = typeof(CoreV3Client)
    .GetMembers(BindingFlags.Instance | BindingFlags.Public)
    .SelectMany(MemberTypes)
    .Where(type => type is not null && type.Assembly.GetName().Name == "KnowledgeClipper.Core.V3.Contracts")
    .Select(type => type!.FullName)
    .Distinct(StringComparer.Ordinal)
    .OrderBy(value => value, StringComparer.Ordinal)
    .ToArray();
var handle = typeof(TypedArtifactHandle);
var requestLimitCode = await ProbeOversizedRequestAsync();
var responseLimitCode = await ProbeOversizedResponseAsync();
var result = new
{
    schemaVersion = "quality-v3-core-sdk-public-consumer-probe.v1",
    lowLevelProtocolClientPublic = protocolClient.IsPublic,
    clientFacadeProtocolTypeLeaks = facadeContractLeaks,
    artifactHandlePublicConstructors = handle.GetConstructors(BindingFlags.Instance | BindingFlags.Public).Length,
    artifactHandlePublicProperties = handle.GetProperties(BindingFlags.Instance | BindingFlags.Public).Select(property => property.Name).OrderBy(value => value, StringComparer.Ordinal).ToArray(),
    oversizedRequestFailureCode = requestLimitCode,
    oversizedResponseFailureCode = responseLimitCode
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
}

static Type Unwrap(Type type)
{
    while (type.IsGenericType && type.GetGenericArguments().Length == 1)
        type = type.GetGenericArguments()[0];
    return type;
}

static async Task<string?> ProbeOversizedRequestAsync()
{
    var client = new CoreV3Client("quality-v3-no-server", new CoreV3ClientOptions(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(50)));
    try
    {
        await client.PublishTextArtifactAsync(new PublishTextArtifactRequest("oversized-request", ArtifactPurpose.Question, new string('x', 900_000)));
        return null;
    }
    catch (CoreV3ClientException exception)
    {
        return exception.Code;
    }
}

static async Task<string?> ProbeOversizedResponseAsync()
{
    var pipeName = "QualityV3.CoreSdk.Oversized." + Guid.NewGuid().ToString("N");
    var server = Task.Run(async () =>
    {
        await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.WaitForConnectionAsync();
        var one = new byte[1];
        while (true)
        {
            var count = await pipe.ReadAsync(one);
            if (count == 0 || one[0] == (byte)'\n') break;
        }
        var oversized = Encoding.UTF8.GetBytes(new string('x', 1024 * 1024 + 1) + "\n");
        try
        {
            await pipe.WriteAsync(oversized);
            await pipe.FlushAsync();
        }
        catch (IOException)
        {
        }
    });
    var client = new CoreV3Client(pipeName, new CoreV3ClientOptions(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(61), TimeSpan.FromMilliseconds(50)));
    try
    {
        await client.GetCapabilitiesAsync();
        return null;
    }
    catch (CoreV3ClientException exception)
    {
        return exception.Code;
    }
    finally
    {
        await server;
    }
}
