using AviUtl2MCP.Application.Errors;

namespace AviUtl2MCP.Application.Instances;

public sealed class InstanceSelector : IInstanceSelector
{
    public ValueTask<ApplicationResult<InstanceDescriptor>> SelectInstanceAsync(
        InstanceSelectionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        InstanceDescriptor[] candidates = request.Candidates
            .Where(candidate => candidate.IsAvailable)
            .ToArray();
        Guid? locatorInstanceId;
        try
        {
            locatorInstanceId = ResolveLocatorInstanceId(request);
        }
        catch (ArgumentException exception)
        {
            return ValueTask.FromResult(ApplicationResult.Failure<InstanceDescriptor>(
                ApplicationErrors.CreateInvalidArgument(exception.Message)));
        }
        if (request.RequestedInstanceId.HasValue
            && locatorInstanceId.HasValue
            && request.RequestedInstanceId != locatorInstanceId)
        {
            return ValueTask.FromResult(ApplicationResult.Failure<InstanceDescriptor>(
                ApplicationErrors.CreateInvalidArgument("Top-level instanceId does not match the object locator instanceId.")));
        }

        Guid? selectedInstanceId = request.RequestedInstanceId
            ?? locatorInstanceId
            ?? request.EnvironmentInstanceId;
        if (selectedInstanceId.HasValue)
        {
            InstanceDescriptor? selected = candidates.FirstOrDefault(
                candidate => candidate.InstanceId == selectedInstanceId.Value);
            ApplicationResult<InstanceDescriptor> result = selected is null
                ? ApplicationResult.Failure<InstanceDescriptor>(ApplicationErrors.CreateAviUtlNotRunning())
                : ApplicationResult.Success(selected);
            return ValueTask.FromResult(result);
        }

        return candidates.Length switch
        {
            0 => ValueTask.FromResult(ApplicationResult.Failure<InstanceDescriptor>(ApplicationErrors.CreateAviUtlNotRunning())),
            1 => ValueTask.FromResult(ApplicationResult.Success(candidates[0])),
            _ => ValueTask.FromResult(ApplicationResult.Failure<InstanceDescriptor>(
                ApplicationErrors.CreateInstanceAmbiguous(candidates.Select(candidate => candidate.InstanceId).ToArray()))),
        };
    }

    private static Guid? ResolveLocatorInstanceId(InstanceSelectionRequest request)
    {
        Guid[] locatorInstanceIds = request.Locators
            .Select(locator => locator.InstanceId)
            .Distinct()
            .ToArray();
        return locatorInstanceIds.Length switch
        {
            0 => null,
            1 => locatorInstanceIds[0],
            _ => throw new ArgumentException("Object locators must refer to one AviUtl2 instance.", nameof(request)),
        };
    }
}
