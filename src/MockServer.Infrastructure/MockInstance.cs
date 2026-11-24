namespace MockServer.Infrastructure;

public sealed class MockInstance
{
    public const string Header = "X-Simple-Mock-Instance";
    public string Id { get; } = Guid.NewGuid().ToString("N");
}
