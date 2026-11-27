using System.Text.Json;
using System.Text.RegularExpressions;
using Hive.Contracts;
using Hive.Domain.Audit;
using Hive.Domain.Devices;
using Hive.Domain.Enrollment;
using Hive.Domain.Identity;
using Hive.Domain.Software;
using Hive.Infrastructure.Repositories;
using Hive.Server.Authentication;
using Hive.Server.Middleware;

namespace Hive.Server.Endpoints;

public static partial class SoftwareEndpoints
{
    private const int MaxInventoryItems = 2000;
    private const int MaxDisplayNameLength = 256;
    private const int MaxPublisherLength = 256;
    private const int MaxVersionLength = 128;
    private const int MaxMetadataLength = 64;

    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex Sha256HexRegex();

    public static RouteGroupBuilder MapSoftwareEndpoints(this RouteGroupBuilder api)
    {
        api.MapPut("/agent/inventory", UpsertAgentInventory)
            .RequireAuthorization(HiveAuthPolicies.DeviceOnly);

        api.MapGet("/software/products", ListProducts)
            .RequireAuthorization(HiveAuthPolicies.UserOnly);

        api.MapPost("/software/products", CreateProduct)
            .RequireAuthorization(HiveAuthPolicies.UserOnly);

        api.MapGet("/software/releases", ListReleases)
            .RequireAuthorization(HiveAuthPolicies.UserOnly);

        api.MapPost("/software/releases", CreateRelease)
            .RequireAuthorization(HiveAuthPolicies.UserOnly);

        api.MapPost("/software/releases/{id:guid}/approve", ApproveRelease)
            .RequireAuthorization(HiveAuthPolicies.UserOnly);

        api.MapPost("/software/releases/{id:guid}/withdraw", WithdrawRelease)
            .RequireAuthorization(HiveAuthPolicies.UserOnly);

        api.MapGet("/software/exemptions", ListExemptions)
            .RequireAuthorization(HiveAuthPolicies.UserOnly);

        api.MapPost("/software/exemptions", CreateExemption)
            .RequireAuthorization(HiveAuthPolicies.UserOnly);

        api.MapPost("/software/exemptions/{id:guid}/revoke", RevokeExemption)
            .RequireAuthorization(HiveAuthPolicies.UserOnly);

        api.MapGet("/software/compliance", GetFleetCompliance)
            .RequireAuthorization(HiveAuthPolicies.UserOnly);

        api.MapGet("/workstations/{id:guid}/compliance", GetWorkstationCompliance)
            .RequireAuthorization(HiveAuthPolicies.UserOnly);

        return api;
    }

    private static async Task<IResult> UpsertAgentInventory(
        InventorySnapshotRequest? request,
        HttpContext context,
        IDeviceRepository deviceRepository,
        ISoftwareRepository softwareRepository,
        IOutboxRepository outboxRepository,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var actor = context.User.ToActorIdentity();
        if (!actor.IsDevice || actor.DeviceId is not Guid deviceId || !ScopeAuthorizationService.CanSendHeartbeat(actor, deviceId))
        {
            return HiveApiEndpoints.ForbiddenProblem("Valid device certificate authentication is required.");
        }

        var existing = await deviceRepository.GetByIdAsync(deviceId, cancellationToken);
        if (existing is null || existing.EnrollmentState != EnrollmentState.Active)
        {
            return HiveApiEndpoints.ForbiddenProblem("Device is not active.");
        }

        if (request is null || request.Items is null)
        {
            return HiveApiEndpoints.ValidationProblem("Inventory payload and Items list are required.");
        }

        if (request.Items.Count > MaxInventoryItems)
        {
            return HiveApiEndpoints.ValidationProblem($"Inventory snapshot cannot exceed {MaxInventoryItems} items.");
        }

        if (request.Items.Any(i => i is null))
        {
            return HiveApiEndpoints.ValidationProblem("Inventory items must not contain null entries.");
        }

        var nowUtc = timeProvider.GetUtcNow();
        var sanitizedItems = request.Items
            .Select(item => SanitizeInstallation(item, nowUtc))
            .ToArray();

        var record = new DeviceInventoryRecord(
            deviceId,
            request.DetectionRevision,
            request.IsComplete,
            request.CapturedAtUtc,
            nowUtc,
            sanitizedItems);

        await softwareRepository.UpsertDeviceInventoryAsync(record, cancellationToken);

        await outboxRepository.EnqueueAsync(
            new OutboxEventRecord(
                Guid.NewGuid(),
                "DeviceInventoryUpdated",
                existing.DeviceId,
                existing.RowVersion,
                existing.SiteId,
                existing.AssignedUserSid,
                nowUtc),
            cancellationToken);

        return Results.Ok(new InventorySnapshotResponse(sanitizedItems.Length, nowUtc));
    }

    private static async Task<IResult> ListProducts(
        HttpContext context,
        ISoftwareRepository softwareRepository,
        CancellationToken cancellationToken)
    {
        var actor = context.User.ToActorIdentity();
        if (!ScopeAuthorizationService.CanViewSoftwareCatalogue(actor))
        {
            return HiveApiEndpoints.ForbiddenProblem("Caller is not authorized to view the software catalogue.");
        }

        var products = await softwareRepository.ListProductsAsync(cancellationToken);
        return Results.Ok(products.Select(ToProductDto).ToArray());
    }

    private static async Task<IResult> CreateProduct(
        CreateSoftwareProductRequest request,
        HttpContext context,
        ISoftwareRepository softwareRepository,
        IAuditRepository auditRepository,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var actor = context.User.ToActorIdentity();
        if (!ScopeAuthorizationService.CanManageSoftwareCatalogue(actor))
        {
            return HiveApiEndpoints.ForbiddenProblem("Caller is not authorized to manage software products.");
        }

        if (string.IsNullOrWhiteSpace(request.Name) ||
            string.IsNullOrWhiteSpace(request.Publisher) ||
            string.IsNullOrWhiteSpace(request.MatchPattern))
        {
            return HiveApiEndpoints.ValidationProblem("Name, Publisher, and MatchPattern are required.");
        }

        if (!Enum.TryParse<VersionScheme>(request.VersionScheme?.Trim(), ignoreCase: true, out var versionScheme) ||
            !Enum.IsDefined(versionScheme))
        {
            return HiveApiEndpoints.ValidationProblem("VersionScheme must be one of NumericSegments, SemVer, or Exact.");
        }

        var processNames = (request.ProtectedProcessNames ?? Array.Empty<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => BoundRequired(p, MaxDisplayNameLength))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var nowUtc = timeProvider.GetUtcNow();
        var product = new SoftwareProduct(
            Guid.NewGuid(),
            BoundRequired(request.Name, MaxDisplayNameLength),
            BoundRequired(request.Publisher, MaxPublisherLength),
            versionScheme,
            BoundRequired(request.MatchPattern, MaxDisplayNameLength),
            DetectionRevision: 1,
            processNames,
            nowUtc);

        await softwareRepository.CreateProductAsync(product, cancellationToken);

        await auditRepository.AppendAsync(
            AuditEvent.Create(
                "User",
                actor.UserSid,
                "SoftwareProductCreated",
                "SoftwareProduct",
                product.ProductId.ToString("D"),
                "Success",
                null,
                CorrelationIdMiddleware.GetCorrelationId(context),
                nowUtc,
                JsonSerializer.Serialize(new
                {
                    name = product.Name,
                    publisher = product.Publisher,
                    versionScheme = product.VersionScheme.ToString()
                })),
            cancellationToken);

        return Results.Created(
            $"{ProtocolConstants.ApiPrefix}/software/products/{product.ProductId:D}",
            ToProductDto(product));
    }

    private static async Task<IResult> ListReleases(
        Guid? productId,
        HttpContext context,
        ISoftwareRepository softwareRepository,
        CancellationToken cancellationToken)
    {
        var actor = context.User.ToActorIdentity();
        if (!ScopeAuthorizationService.CanViewSoftwareCatalogue(actor))
        {
            return HiveApiEndpoints.ForbiddenProblem("Caller is not authorized to view software releases.");
        }

        var releases = await softwareRepository.ListReleasesAsync(productId, cancellationToken);
        return Results.Ok(releases.Select(ToReleaseDto).ToArray());
    }

    private static async Task<IResult> CreateRelease(
        CreateSoftwareReleaseRequest request,
        HttpContext context,
        ISoftwareRepository softwareRepository,
        IAuditRepository auditRepository,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var actor = context.User.ToActorIdentity();
        if (!ScopeAuthorizationService.CanManageSoftwareCatalogue(actor))
        {
            return HiveApiEndpoints.ForbiddenProblem("Caller is not authorized to draft software releases.");
        }

        if (string.IsNullOrWhiteSpace(request.VersionRaw) ||
            string.IsNullOrWhiteSpace(request.ValidationReference))
        {
            return HiveApiEndpoints.ValidationProblem("VersionRaw and ValidationReference are required.");
        }

        string? normalizedSha256 = null;
        if (!string.IsNullOrWhiteSpace(request.PackageSha256))
        {
            var trimmedSha = request.PackageSha256.Trim();
            if (!Sha256HexRegex().IsMatch(trimmedSha))
            {
                return HiveApiEndpoints.ValidationProblem("PackageSha256 must be a 64-character hexadecimal string.");
            }

            normalizedSha256 = trimmedSha.ToLowerInvariant();
        }

        var product = await softwareRepository.GetProductByIdAsync(request.ProductId, cancellationToken);
        if (product is null)
        {
            return HiveApiEndpoints.Problem(
                StatusCodes.Status404NotFound,
                "Software product not found",
                $"Software product '{request.ProductId}' was not found.");
        }

        var nowUtc = timeProvider.GetUtcNow();
        var release = new SoftwareRelease(
            Guid.NewGuid(),
            product.ProductId,
            BoundRequired(request.VersionRaw, MaxVersionLength),
            product.VersionScheme,
            ReleaseApprovalState.Draft,
            BoundRequired(request.ValidationReference, MaxDisplayNameLength),
            normalizedSha256,
            BoundOptional(request.ManifestDigest, MaxDisplayNameLength),
            actor.UserSid,
            null,
            nowUtc,
            null,
            null,
            null);

        await softwareRepository.CreateReleaseAsync(release, cancellationToken);

        await auditRepository.AppendAsync(
            AuditEvent.Create(
                "User",
                actor.UserSid,
                "SoftwareReleaseDrafted",
                "SoftwareRelease",
                release.ReleaseId.ToString("D"),
                "Success",
                null,
                CorrelationIdMiddleware.GetCorrelationId(context),
                nowUtc,
                JsonSerializer.Serialize(new
                {
                    productId = release.ProductId,
                    versionRaw = release.VersionRaw
                })),
            cancellationToken);

        return Results.Created(
            $"{ProtocolConstants.ApiPrefix}/software/releases/{release.ReleaseId:D}",
            ToReleaseDto(release));
    }

    private static async Task<IResult> ApproveRelease(
        Guid id,
        ApproveSoftwareReleaseRequest? request,
        HttpContext context,
        ISoftwareRepository softwareRepository,
        IAuditRepository auditRepository,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var existing = await softwareRepository.GetReleaseByIdAsync(id, cancellationToken);
        if (existing is null)
        {
            return HiveApiEndpoints.Problem(
                StatusCodes.Status404NotFound,
                "Software release not found",
                $"Software release '{id}' was not found.");
        }

        var actor = context.User.ToActorIdentity();
        if (!ScopeAuthorizationService.CanApproveSoftwareRelease(actor, existing.CreatedBySid))
        {
            return HiveApiEndpoints.ForbiddenProblem(
                "Caller must hold SoftwareApprover or Administrator role and cannot approve their own drafted release.");
        }

        if (existing.ApprovalState != ReleaseApprovalState.Draft)
        {
            return HiveApiEndpoints.Problem(
                StatusCodes.Status409Conflict,
                "Release state conflict",
                $"Only Draft releases can be approved; current state is '{existing.ApprovalState}'.");
        }

        var nowUtc = timeProvider.GetUtcNow();
        var updated = await softwareRepository.ApproveReleaseAsync(id, actor.UserSid, nowUtc, cancellationToken);
        if (updated is null)
        {
            return HiveApiEndpoints.Problem(
                StatusCodes.Status409Conflict,
                "Release state conflict",
                "The software release state changed concurrently and could not be approved.");
        }

        await auditRepository.AppendAsync(
            AuditEvent.Create(
                "User",
                actor.UserSid,
                "SoftwareReleaseApproved",
                "SoftwareRelease",
                updated.ReleaseId.ToString("D"),
                "Success",
                null,
                CorrelationIdMiddleware.GetCorrelationId(context),
                nowUtc,
                JsonSerializer.Serialize(new
                {
                    productId = updated.ProductId,
                    versionRaw = updated.VersionRaw,
                    notes = BoundOptional(request?.Notes, MaxDisplayNameLength)
                })),
            cancellationToken);

        return Results.Ok(ToReleaseDto(updated));
    }

    private static async Task<IResult> WithdrawRelease(
        Guid id,
        WithdrawSoftwareReleaseRequest? request,
        HttpContext context,
        ISoftwareRepository softwareRepository,
        IAuditRepository auditRepository,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var actor = context.User.ToActorIdentity();
        if (actor.IsDevice ||
            !actor.HasAnyRole(HiveRole.SoftwareAdmin, HiveRole.SoftwareApprover, HiveRole.Administrator))
        {
            return HiveApiEndpoints.ForbiddenProblem("Caller is not authorized to withdraw software releases.");
        }

        if (string.IsNullOrWhiteSpace(request?.Reason))
        {
            return HiveApiEndpoints.ValidationProblem("Withdrawal Reason is required.");
        }

        var existing = await softwareRepository.GetReleaseByIdAsync(id, cancellationToken);
        if (existing is null)
        {
            return HiveApiEndpoints.Problem(
                StatusCodes.Status404NotFound,
                "Software release not found",
                $"Software release '{id}' was not found.");
        }

        if (existing.ApprovalState == ReleaseApprovalState.Withdrawn)
        {
            return HiveApiEndpoints.Problem(
                StatusCodes.Status409Conflict,
                "Release already withdrawn",
                "This software release has already been withdrawn.");
        }

        var nowUtc = timeProvider.GetUtcNow();
        var reason = BoundRequired(request.Reason, MaxDisplayNameLength);
        var updated = await softwareRepository.WithdrawReleaseAsync(id, reason, nowUtc, cancellationToken);
        if (updated is null)
        {
            return HiveApiEndpoints.Problem(
                StatusCodes.Status409Conflict,
                "Release state conflict",
                "The software release could not be withdrawn because its state changed concurrently.");
        }

        await auditRepository.AppendAsync(
            AuditEvent.Create(
                "User",
                actor.UserSid,
                "SoftwareReleaseWithdrawn",
                "SoftwareRelease",
                updated.ReleaseId.ToString("D"),
                "Success",
                null,
                CorrelationIdMiddleware.GetCorrelationId(context),
                nowUtc,
                JsonSerializer.Serialize(new
                {
                    productId = updated.ProductId,
                    versionRaw = updated.VersionRaw,
                    reason
                })),
            cancellationToken);

        return Results.Ok(ToReleaseDto(updated));
    }

    private static async Task<IResult> ListExemptions(
        Guid? productId,
        Guid? deviceId,
        HttpContext context,
        ISoftwareRepository softwareRepository,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var actor = context.User.ToActorIdentity();
        if (!ScopeAuthorizationService.CanViewSoftwareCatalogue(actor))
        {
            return HiveApiEndpoints.ForbiddenProblem("Caller is not authorized to view software exemptions.");
        }

        var nowUtc = timeProvider.GetUtcNow();
        var exemptions = await softwareRepository.ListExemptionsAsync(productId, deviceId, cancellationToken);
        return Results.Ok(exemptions.Select(e => ToExemptionDto(e, nowUtc)).ToArray());
    }

    private static async Task<IResult> CreateExemption(
        CreateSoftwareExemptionRequest request,
        HttpContext context,
        IDeviceRepository deviceRepository,
        ISoftwareRepository softwareRepository,
        IAuditRepository auditRepository,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var actor = context.User.ToActorIdentity();
        if (!ScopeAuthorizationService.CanManageSoftwareExemptions(actor))
        {
            return HiveApiEndpoints.ForbiddenProblem("Caller is not authorized to create software exemptions.");
        }

        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return HiveApiEndpoints.ValidationProblem("Exemption Reason is required.");
        }

        if (request.ExpiresAtUtc <= request.StartsAtUtc)
        {
            return HiveApiEndpoints.ValidationProblem("ExpiresAtUtc must be strictly greater than StartsAtUtc.");
        }

        var product = await softwareRepository.GetProductByIdAsync(request.ProductId, cancellationToken);
        if (product is null)
        {
            return HiveApiEndpoints.Problem(
                StatusCodes.Status404NotFound,
                "Software product not found",
                $"Software product '{request.ProductId}' was not found.");
        }

        Device? targetDevice = null;
        if (request.DeviceId is Guid devId)
        {
            targetDevice = await deviceRepository.GetByIdAsync(devId, cancellationToken);
            if (targetDevice is null)
            {
                return HiveApiEndpoints.Problem(
                    StatusCodes.Status404NotFound,
                    "Workstation not found",
                    $"Workstation '{devId}' was not found.");
            }
        }

        var nowUtc = timeProvider.GetUtcNow();
        var exemption = new SoftwareExemption(
            Guid.NewGuid(),
            request.DeviceId,
            product.ProductId,
            BoundOptional(request.AllowedVersionRaw, MaxVersionLength),
            BoundRequired(request.Reason, MaxDisplayNameLength),
            actor.UserSid,
            request.StartsAtUtc,
            request.ExpiresAtUtc,
            null,
            null,
            null);

        await softwareRepository.CreateExemptionAsync(exemption, cancellationToken);

        await auditRepository.AppendAsync(
            AuditEvent.Create(
                "User",
                actor.UserSid,
                "SoftwareExemptionCreated",
                "SoftwareExemption",
                exemption.ExemptionId.ToString("D"),
                "Success",
                targetDevice?.SiteId,
                CorrelationIdMiddleware.GetCorrelationId(context),
                nowUtc,
                JsonSerializer.Serialize(new
                {
                    productId = exemption.ProductId,
                    deviceId = exemption.DeviceId,
                    allowedVersionRaw = exemption.AllowedVersionRaw,
                    startsAtUtc = exemption.StartsAtUtc,
                    expiresAtUtc = exemption.ExpiresAtUtc
                })),
            cancellationToken);

        return Results.Created(
            $"{ProtocolConstants.ApiPrefix}/software/exemptions/{exemption.ExemptionId:D}",
            ToExemptionDto(exemption, nowUtc));
    }

    private static async Task<IResult> RevokeExemption(
        Guid id,
        RevokeSoftwareExemptionRequest? request,
        HttpContext context,
        IDeviceRepository deviceRepository,
        ISoftwareRepository softwareRepository,
        IAuditRepository auditRepository,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var actor = context.User.ToActorIdentity();
        if (!ScopeAuthorizationService.CanManageSoftwareExemptions(actor))
        {
            return HiveApiEndpoints.ForbiddenProblem("Caller is not authorized to revoke software exemptions.");
        }

        if (string.IsNullOrWhiteSpace(request?.Reason))
        {
            return HiveApiEndpoints.ValidationProblem("Revocation Reason is required.");
        }

        var existing = await softwareRepository.GetExemptionByIdAsync(id, cancellationToken);
        if (existing is null)
        {
            return HiveApiEndpoints.Problem(
                StatusCodes.Status404NotFound,
                "Software exemption not found",
                $"Software exemption '{id}' was not found.");
        }

        if (existing.RevokedAtUtc is not null)
        {
            return HiveApiEndpoints.Problem(
                StatusCodes.Status409Conflict,
                "Exemption already revoked",
                "This software exemption has already been revoked.");
        }

        var nowUtc = timeProvider.GetUtcNow();
        var reason = BoundRequired(request.Reason, MaxDisplayNameLength);
        var updated = await softwareRepository.RevokeExemptionAsync(id, actor.UserSid, reason, nowUtc, cancellationToken);
        if (updated is null)
        {
            return HiveApiEndpoints.Problem(
                StatusCodes.Status409Conflict,
                "Exemption state conflict",
                "The software exemption was revoked concurrently.");
        }

        string? siteId = null;
        if (updated.DeviceId is Guid devId)
        {
            var device = await deviceRepository.GetByIdAsync(devId, cancellationToken);
            siteId = device?.SiteId;
        }

        await auditRepository.AppendAsync(
            AuditEvent.Create(
                "User",
                actor.UserSid,
                "SoftwareExemptionRevoked",
                "SoftwareExemption",
                updated.ExemptionId.ToString("D"),
                "Success",
                siteId,
                CorrelationIdMiddleware.GetCorrelationId(context),
                nowUtc,
                JsonSerializer.Serialize(new
                {
                    productId = updated.ProductId,
                    deviceId = updated.DeviceId,
                    reason
                })),
            cancellationToken);

        return Results.Ok(ToExemptionDto(updated, nowUtc));
    }

    private static async Task<IResult> GetFleetCompliance(
        string? siteId,
        HttpContext context,
        IDeviceRepository deviceRepository,
        ISoftwareRepository softwareRepository,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var actor = context.User.ToActorIdentity();
        if (!ScopeAuthorizationService.CanViewFleetCompliance(actor))
        {
            return HiveApiEndpoints.ForbiddenProblem("Caller is not authorized to view fleet software compliance.");
        }

        if (!string.IsNullOrWhiteSpace(siteId) && !actor.CanAccessSite(siteId.Trim()))
        {
            return HiveApiEndpoints.ForbiddenProblem("Caller is not authorized for the requested site.");
        }

        var devices = await deviceRepository.ListAsync(
            siteId: string.IsNullOrWhiteSpace(siteId) ? null : siteId.Trim(),
            allowedSiteIds: actor.AllowedSiteIds,
            allSites: actor.AllSitesAccess,
            cancellationToken: cancellationToken);

        var products = await softwareRepository.ListProductsAsync(cancellationToken);
        var releases = await softwareRepository.ListReleasesAsync(cancellationToken: cancellationToken);
        var exemptions = await softwareRepository.ListExemptionsAsync(cancellationToken: cancellationToken);
        var inventories = await softwareRepository.ListDeviceInventoriesAsync(
            devices.Select(d => d.DeviceId).ToArray(),
            cancellationToken);
        var inventoryByDevice = inventories.ToDictionary(i => i.DeviceId);

        var activeReleasesByProduct = BuildActiveApprovedReleaseMap(releases);
        var nowUtc = timeProvider.GetUtcNow();

        var summaries = devices
            .Select(d => EvaluateDeviceSummary(
                d,
                products,
                activeReleasesByProduct,
                exemptions,
                inventoryByDevice.GetValueOrDefault(d.DeviceId),
                nowUtc))
            .ToArray();

        return Results.Ok(summaries);
    }

    private static async Task<IResult> GetWorkstationCompliance(
        Guid id,
        HttpContext context,
        IDeviceRepository deviceRepository,
        ISoftwareRepository softwareRepository,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var device = await deviceRepository.GetByIdAsync(id, cancellationToken);
        if (device is null)
        {
            return HiveApiEndpoints.Problem(
                StatusCodes.Status404NotFound,
                "Workstation not found",
                $"Workstation '{id}' was not found.");
        }

        var actor = context.User.ToActorIdentity();
        if (!ScopeAuthorizationService.CanViewWorkstation(actor, device.SiteId, device.AssignedUserSid))
        {
            return HiveApiEndpoints.ForbiddenProblem("Caller is not authorized to view compliance for this workstation.");
        }

        var products = await softwareRepository.ListProductsAsync(cancellationToken);
        var releases = await softwareRepository.ListReleasesAsync(cancellationToken: cancellationToken);
        var exemptions = await softwareRepository.ListExemptionsAsync(cancellationToken: cancellationToken);
        var inventory = await softwareRepository.GetDeviceInventoryAsync(id, cancellationToken);

        var activeReleasesByProduct = BuildActiveApprovedReleaseMap(releases);
        var summary = EvaluateDeviceSummary(
            device,
            products,
            activeReleasesByProduct,
            exemptions,
            inventory,
            timeProvider.GetUtcNow());

        return Results.Ok(summary);
    }

    private static Dictionary<Guid, SoftwareRelease> BuildActiveApprovedReleaseMap(IReadOnlyList<SoftwareRelease> releases) =>
        releases
            .Where(r => r.ApprovalState == ReleaseApprovalState.Approved)
            .GroupBy(r => r.ProductId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(r => r.ApprovedAtUtc ?? r.CreatedAtUtc)
                    .ThenByDescending(r => r.CreatedAtUtc)
                    .First());

    private static DeviceComplianceSummaryDto EvaluateDeviceSummary(
        Device device,
        IReadOnlyList<SoftwareProduct> products,
        IReadOnlyDictionary<Guid, SoftwareRelease> activeReleasesByProduct,
        IReadOnlyList<SoftwareExemption> exemptions,
        DeviceInventoryRecord? inventory,
        DateTimeOffset nowUtc)
    {
        var items = inventory?.Items ?? Array.Empty<DeviceSoftwareInstallation>();
        var inventoryIsComplete = inventory?.IsComplete ?? false;
        var lastInventoryAtUtc = inventory?.ReceivedAtUtc;

        var productDtos = new List<DeviceProductComplianceDto>(products.Count);
        var matchedInstallations = new HashSet<int>();

        var compliantCount = 0;
        var updateRequiredCount = 0;
        var aheadUnapprovedCount = 0;
        var notInstalledCount = 0;
        var exemptCount = 0;
        var unknownCount = 0;

        foreach (var product in products)
        {
            var matching = new List<DeviceSoftwareInstallation>();
            for (var i = 0; i < items.Count; i++)
            {
                if (SoftwareComplianceEvaluator.MatchesProduct(items[i], product))
                {
                    matching.Add(items[i]);
                    matchedInstallations.Add(i);
                }
            }

            activeReleasesByProduct.TryGetValue(product.ProductId, out var approvedRelease);
            var eval = SoftwareComplianceEvaluator.Evaluate(
                device.DeviceId,
                product,
                approvedRelease,
                matching,
                inventoryIsComplete,
                exemptions,
                nowUtc);

            switch (eval.Status)
            {
                case ComplianceStatus.Compliant:
                    compliantCount++;
                    break;
                case ComplianceStatus.UpdateRequired:
                    updateRequiredCount++;
                    break;
                case ComplianceStatus.AheadUnapproved:
                    aheadUnapprovedCount++;
                    break;
                case ComplianceStatus.NotInstalled:
                    notInstalledCount++;
                    break;
                case ComplianceStatus.Exempt:
                    exemptCount++;
                    break;
                default:
                    unknownCount++;
                    break;
            }

            productDtos.Add(new DeviceProductComplianceDto(
                device.DeviceId,
                device.Hostname,
                device.SiteId,
                product.ProductId,
                product.Name,
                approvedRelease?.VersionRaw,
                eval.ObservedVersionRaw,
                eval.Status.ToString(),
                eval.Reason,
                eval.ActiveExemption?.ExemptionId,
                eval.ActiveExemption?.ExpiresAtUtc,
                lastInventoryAtUtc));
        }

        var unmanaged = items
            .Where((_, idx) => !matchedInstallations.Contains(idx))
            .OrderBy(i => i.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(i => new InstalledSoftwareItemDto(
                i.DisplayName,
                i.Publisher,
                i.ObservedVersionRaw,
                i.DetectionSource,
                i.InstallScope,
                i.Confidence.ToString(),
                i.ObservedAtUtc))
            .ToArray();

        return new DeviceComplianceSummaryDto(
            device.DeviceId,
            device.Hostname,
            device.SiteId,
            compliantCount,
            updateRequiredCount,
            aheadUnapprovedCount,
            notInstalledCount,
            exemptCount,
            unknownCount,
            lastInventoryAtUtc,
            productDtos,
            unmanaged);
    }

    private static DeviceSoftwareInstallation SanitizeInstallation(InstalledSoftwareItemDto dto, DateTimeOffset defaultObservedAtUtc)
    {
        var confidence = Enum.TryParse<DetectionConfidence>(dto.ConfidenceState?.Trim(), ignoreCase: true, out var parsedConfidence) &&
                         Enum.IsDefined(parsedConfidence)
            ? parsedConfidence
            : DetectionConfidence.Unknown;

        var observedAt = dto.ObservedAtUtc == default ? defaultObservedAtUtc : dto.ObservedAtUtc;

        return new DeviceSoftwareInstallation(
            BoundRequired(dto.DisplayName, MaxDisplayNameLength),
            BoundOptional(dto.Publisher, MaxPublisherLength),
            BoundOptional(dto.ObservedVersionRaw, MaxVersionLength),
            BoundOrDefault(dto.DetectionSource, MaxMetadataLength, "Unknown"),
            BoundOrDefault(dto.InstallScope, MaxMetadataLength, "Unknown"),
            confidence,
            observedAt);
    }

    private static string BoundRequired(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static string BoundOrDefault(string? value, int maxLength, string fallback)
    {
        var bounded = BoundOptional(value, maxLength);
        return bounded ?? fallback;
    }

    private static string? BoundOptional(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static SoftwareProductDto ToProductDto(SoftwareProduct product) =>
        new(
            product.ProductId,
            product.Name,
            product.Publisher,
            product.VersionScheme.ToString(),
            product.MatchPattern,
            product.DetectionRevision,
            product.ProtectedProcessNames,
            product.CreatedAtUtc);

    private static SoftwareReleaseDto ToReleaseDto(SoftwareRelease release) =>
        new(
            release.ReleaseId,
            release.ProductId,
            release.VersionRaw,
            release.VersionScheme.ToString(),
            release.ApprovalState.ToString(),
            release.ValidationReference,
            release.PackageSha256,
            release.ManifestDigest,
            release.CreatedBySid,
            release.ApprovedBySid,
            release.CreatedAtUtc,
            release.ApprovedAtUtc,
            release.WithdrawnAtUtc,
            release.WithdrawnReason);

    private static SoftwareExemptionDto ToExemptionDto(SoftwareExemption exemption, DateTimeOffset nowUtc) =>
        new(
            exemption.ExemptionId,
            exemption.DeviceId,
            exemption.ProductId,
            exemption.AllowedVersionRaw,
            exemption.Reason,
            exemption.ApprovedBySid,
            exemption.StartsAtUtc,
            exemption.ExpiresAtUtc,
            exemption.RevokedAtUtc,
            exemption.RevokedBySid,
            exemption.RevokedReason,
            exemption.IsActiveAt(nowUtc));
}
