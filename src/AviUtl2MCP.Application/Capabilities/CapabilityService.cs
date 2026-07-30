using AviUtl2MCP.Application.Contracts;

namespace AviUtl2MCP.Application.Capabilities;

public static class CapabilityService
{
    private static readonly string[] alwaysAvailableOperations =
    [
        "aviutl_get_status",
        "aviutl_get_capabilities",
        "aviutl_get_logs",
        "aviutl_diagnose",
    ];

    private static readonly string[] bridgeOperations =
    [
        "aviutl_list_effects",
        "aviutl_list_effect_items",
    ];

    private static readonly string[] projectOperations =
    [
        "aviutl_get_project",
        "aviutl_get_timeline",
        "aviutl_find_objects",
        "aviutl_get_object",
        "aviutl_set_cursor",
        "aviutl_render_preview",
    ];

    private static readonly string[] editOperations =
    [
        "aviutl_create_object",
        "aviutl_create_media_object",
        "aviutl_create_alias_object",
        "aviutl_move_object",
        "aviutl_delete_object",
        "aviutl_set_object_name",
        "aviutl_create_object_section",
        "aviutl_delete_object_section",
        "aviutl_move_object_section",
        "aviutl_set_effect_item",
        "aviutl_set_effect_state",
        "aviutl_set_layer",
        "aviutl_execute_batch",
    ];

    private static readonly string[] psdToolKitOperations =
    [
        "aviutl_psd_setup",
        "aviutl_psd_set_character",
        "aviutl_psd_set_layer_state",
    ];

    public static CapabilitiesData GetCapabilities(CapabilityEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        List<CapabilityOperation> operations = [];
        AddOperations(operations, alwaysAvailableOperations, true, null);
        AddOperations(
            operations,
            bridgeOperations,
            environment.IsBridgeReady,
            environment.IsBridgeReady ? null : "bridge_not_connected");

        bool hasProject = environment.IsBridgeReady && environment.IsProjectOpen;
        AddOperations(
            operations,
            projectOperations,
            hasProject,
            GetProjectReason(environment));

        bool canEdit = hasProject && environment.CanEdit;
        AddOperations(
            operations,
            editOperations,
            canEdit,
            GetEditReason(environment));

        bool canSaveProject = canEdit && environment.IsProjectSaved;
        AddOperations(
            operations,
            ["aviutl_save_project"],
            canSaveProject,
            GetSaveProjectReason(environment));

        bool canUsePsdToolKit = canEdit && environment.HasPsdToolKit;
        AddOperations(
            operations,
            psdToolKitOperations,
            canUsePsdToolKit,
            GetPsdToolKitReason(environment));

        bool canValidatePsd = hasProject && environment.HasPsdToolKit;
        AddOperations(
            operations,
            ["aviutl_psd_validate"],
            canValidatePsd,
            GetPsdValidationReason(environment));

        bool canCreatePsd = canEdit && environment.HasGcmzDrops;
        AddOperations(
            operations,
            ["aviutl_psd_create"],
            canCreatePsd,
            GetGcmzDropsReason(environment));

        bool canCreateVoice = canEdit && environment.HasPsdToolKit && environment.HasGcmzDrops;
        AddOperations(
            operations,
            ["aviutl_psd_create_voice"],
            canCreateVoice,
            GetVoiceReason(environment));

        return new CapabilitiesData(
            operations.OrderBy(operation => operation.Name, StringComparer.Ordinal).ToArray(),
            environment.Versions,
            CreateLimits());
    }

    private static void AddOperations(
        List<CapabilityOperation> target,
        IEnumerable<string> names,
        bool isAvailable,
        string? reason)
    {
        foreach (string name in names)
        {
            target.Add(new CapabilityOperation(name, isAvailable, reason, []));
        }
    }

    private static string? GetProjectReason(CapabilityEnvironment environment)
    {
        return !environment.IsBridgeReady ? "bridge_not_connected"
            : !environment.IsProjectOpen ? "project_not_open"
            : null;
    }

    private static string? GetEditReason(CapabilityEnvironment environment)
    {
        return GetProjectReason(environment) ?? (!environment.CanEdit ? "edit_not_available" : null);
    }

    private static string? GetPsdToolKitReason(CapabilityEnvironment environment)
    {
        return GetEditReason(environment) ?? (!environment.HasPsdToolKit ? "psdtoolkit_not_available" : null);
    }

    private static string? GetSaveProjectReason(CapabilityEnvironment environment)
    {
        return GetEditReason(environment)
            ?? (!environment.IsProjectSaved ? "project_path_required" : null);
    }

    private static string? GetGcmzDropsReason(CapabilityEnvironment environment)
    {
        return GetEditReason(environment) ?? (!environment.HasGcmzDrops ? "gcmzdrops_not_available" : null);
    }

    private static string? GetPsdValidationReason(CapabilityEnvironment environment)
    {
        return GetProjectReason(environment) ?? (!environment.HasPsdToolKit ? "psdtoolkit_not_available" : null);
    }

    private static string? GetVoiceReason(CapabilityEnvironment environment)
    {
        return GetPsdToolKitReason(environment) ?? GetGcmzDropsReason(environment);
    }

    private static CapabilityLimits CreateLimits()
    {
        return new CapabilityLimits(
            IpcJsonBytes: 8 * 1024 * 1024,
            IpcBinaryBytes: 16 * 1024 * 1024,
            IpcInFlight: 8,
            BridgeConnections: 1,
            GlobalQueue: 64,
            MutationTombstones: 4096,
            MutationResponseCache: 256,
            TimelineDefaultItems: 100,
            TimelineMaxItems: 1000,
            BatchOperations: 100,
            AliasUtf8Bytes: 1024 * 1024,
            ToolStringUtf8Bytes: 64 * 1024,
            LogDefaultLines: 100,
            LogMaxLines: 2000,
            PreviewMaxWidth: 4096,
            PreviewMaxHeight: 4096,
            PngBytes: 16 * 1024 * 1024,
            StatusTimeoutMs: 2000,
            EditTimeoutMs: 10_000,
            PreviewTimeoutMs: 30_000,
            PagingCursorTtlSeconds: 300);
    }
}
