using System.Text.Json.Nodes;
using AviUtl2MCP.Application.Contracts;
using AviUtl2MCP.Application.Errors;
using AviUtl2MCP.Application.Requests;
using AviUtl2MCP.Application.Results;

namespace AviUtl2MCP.UnitTests;

[TestClass]
public sealed class ApplicationFoundationTests
{
    [TestMethod]
    public void CreateEnvelopePreservesPartialFailureData()
    {
        // Arrange
        ToolWarning warning = new("partial", "Some items were applied.", new Dictionary<string, JsonNode?>());
        ApplicationError error = ApplicationErrors.CreateError("partial_operation", "Operation partially applied.");
        ApplicationResult<string> result = ApplicationResult.Failure(error, "post-state", [warning]);
        using RequestContext context = new RequestContextFactory(
            new FixedTimeProvider(TestTime.CreateReferenceUtc()))
            .CreateContext(null, 1000, 2000, CancellationToken.None);

        // Act
        ToolEnvelope<string> envelope = ToolResultFactory.CreateEnvelope(result, context);

        // Assert
        Assert.IsFalse(envelope.Ok);
        Assert.AreEqual("post-state", envelope.Data);
        Assert.AreEqual("partial_operation", envelope.Error!.Code);
        Assert.HasCount(1, envelope.Warnings);
    }

    [TestMethod]
    public void CreateContextUsesVersionSevenCorrelationAndDeadline()
    {
        // Arrange
        DateTimeOffset now = TestTime.CreateReferenceUtc();
        RequestContextFactory factory = new(new FixedTimeProvider(now));

        // Act
        using RequestContext context = factory.CreateContext(Guid.NewGuid(), 1500, 2000, CancellationToken.None);

        // Assert
        Assert.AreEqual(7, context.CorrelationId.Version);
        Assert.AreEqual(now.AddMilliseconds(1500), context.Deadline);
        Assert.AreEqual(1500, context.TimeoutMs);
        Assert.IsFalse(context.CancellationToken.IsCancellationRequested);
    }
}
