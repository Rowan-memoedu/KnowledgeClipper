using KnowledgeClipper.Core.V3.Client;

var options = new CoreV3ClientOptions(
    RequestTimeout: TimeSpan.FromSeconds(1),
    ObservationTimeout: TimeSpan.FromSeconds(2),
    PollInterval: TimeSpan.FromMilliseconds(50));
var client = new CoreV3Client("sdk-boundary-positive-consumer", options);
var snapshot = new CapabilitySnapshot(
    "consumer-catalog",
    new string('0', 64),
    new[] { new CapabilityAvailability("content.settings.describe", true, null) });
var request = new PublishTextArtifactRequest("consumer-request", ArtifactPurpose.Question, "2 + 2 = ?");

if (client is null || snapshot.Operations.Count != 1 || request.Purpose != ArtifactPurpose.Question)
{
    return 1;
}

Console.WriteLine("SDK_POSITIVE_CONSUMER_OK");
return 0;
