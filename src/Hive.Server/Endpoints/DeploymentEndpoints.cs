using System.Text.Json;
using Hive.Contracts;
using Hive.Domain.Audit;
using Hive.Domain.Deployments;
using Hive.Domain.Devices;
using Hive.Domain.Identity;
using Hive.Domain.Software;
using Hive.Infrastructure.Repositories;
using Hive.Server.Authentication;
using Hive.Server.Middleware;
using Hive.Server.Services;
using Microsoft.Net.Http.Headers;

namespace Hive.Server.Endpoints;

public static class DeploymentEndpoints
{
    private const int MaxFileNameLength = 256;
    private const int MaxReasonLength = 128;
    private const int MaxMessageLength = 1024;
    private static readonly TimeSpan DefaultLeaseDuration = TimeSpan.FromMinutes(15);

    public static RouteGroupBuilder MapDeploymentEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/packages", UploadPackage)
            .RequireAuthorization(HiveAuthPolicies.UserOnly)
            .DisableAntiforgery();

        api.MapGet("/packages/{packageId:guid}/content", DownloadPackageContent)
            .RequireAuthorization(HiveAuthPolicies.UserOrDevice);

        api.MapPost("/software/releases/{releaseId:guid}/sign", SignRelease)
            .RequireAuthorization(HiveAuthPolicies.UserOnly);

        api.MapGet("/deployments", ListDeploymentJobs)
            .RequireAuthorization(HiveAuthPolicies.UserOnly);

        api.MapGet("/deployments/{jobId:guid}", GetDeploymentJob)
            .RequireAuthorization(HiveAuthPolicies.UserOnly);

        api.MapPost("/deployments", CreateDeploymentJob)
            .RequireAuthorization(HiveAuthPolicies.UserOnly);

        api.MapPost("/deployments/{jobId:guid}/cancel", CancelDeploymentJob)
            .RequireAuthorization(HiveAuthPolicies.UserOnly);

        api.MapGet("/agent/sync", GetAgentSync)
            .RequireAuthorization(HiveAuthPolicies.DeviceOnly);

        api.MapPost("/agent/deployments/{targetId:guid}/lease", AcquireDeploymentLease)
            .RequireAuthorization(HiveAuthPolicies.DeviceOnly);

        api.MapPost("/agent/deployments/{targetId:guid}/events", RecordDeploymentEvent)
            .RequireAuthorization(HiveAuthPolicies.DeviceOnly);

        return api;
    }

    private static async Task<IResult> UploadPackage(
        HttpContext context,
        IPackageArtifactStore artifactStore,
        IDeploymentRepository deploymentRepository,
        IAuditRepository auditRepository,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var actor = context.User.ToActorIdentity();
        if (!ScopeAuthorizationService.CanManageSoftwareCatalogue(actor))
        {
            return HiveApiEndpoints.ForbiddenProblem("Caller is not authorized to upload software packages.");
        }

        if (!context.Request.HasFormContentType)
        {
            return HiveApiEndpoints.ValidationProblem("Multipart form-data payload with 'file' is required.");
        }

        var form = await context.Request.ReadFormAsync(cancellationToken);
        var file = form.Files.GetFile("file") ?? (form.Files.Count == 1 ? form.Files[0] : null);
        if (file is null || file.Length <= 0)
        {
            return HiveApiEndpoints.ValidationProblem("A non-empty package 'file' is required.");
        }

        var rawName = Path.GetFileName(file.FileName);
        var fileName = string.IsNullOrWhiteSpace(rawName)
            ? "package.bin"
            : rawName.Length <= MaxFileNameLength ? rawName : rawName[..MaxFileNameLength];

        StoredPackageContent stored;
        await using (var uploadStream = file.OpenReadStream())
        {
            stored = await artifactStore.StoreAsync(uploadStream, fileName, cancellationToken);
        }

        if (stored.SizeBytes <= 0)
        {
            return HiveApiEndpoints.ValidationProblem("Uploaded package file must not be empty.");
        }

        var nowUtc = timeProvider.GetUtcNow();
        var artifact = new PackageArtifact(
            Guid.NewGuid(),
            fileName,
            stored.Sha256,
            stored.SizeBytes,
            stored.StoragePath,
            actor.UserSid,
            nowUtc);

        await deploymentRepository.CreatePackageAsync(artifact, cancellationToken);

        await auditRepository.AppendAsync(
            AuditEvent.Create(
                "User",
                actor.UserSid,
                "PackageUploaded",
                "Package",
                artifact.Id.ToString("D"),
                "Success",
                null,
                CorrelationIdMiddleware.GetCorrelationId(context),
                nowUtc,
                JsonSerializer.Serialize(new
                {
                    fileName = artifact.FileName,
                    sha256 = artifact.Sha256,
                    sizeBytes = artifact.SizeBytes
                })),
            cancellationToken);

        var contentType = string.IsNullOrWhiteSpace(file.ContentType)
            ? "application/octet-stream"
            : file.ContentType;

        return Results.Created(
            $"{ProtocolConstants.ApiPrefix}/packages/{artifact.Id:D}",
            new PackageUploadResponse(
                artifact.Id,
                artifact.FileName,
                artifact.Sha256,
                artifact.SizeBytes,
                contentType,
                artifact.UploadedUtc));
    }

    private static async Task<IResult> DownloadPackageContent(
        Guid packageId,
        HttpContext context,
        IDeviceRepository deviceRepository,
        IPackageArtifactStore artifactStore,
        IDeploymentRepository deploymentRepository,
        CancellationToken cancellationToken)
    {
        var actor = context.User.ToActorIdentity();
        if (actor.IsDevice)
        {
            if (actor.DeviceId is not Guid deviceId || !ScopeAuthorizationService.CanSendHeartbeat(actor, deviceId))
            {
                return HiveApiEndpoints.ForbiddenProblem("Valid device certificate authentication is required.");
            }

            var device = await deviceRepository.GetByIdAsync(deviceId, cancellationToken);
            if (device is null || device.EnrollmentState != EnrollmentState.Active)
            {
                return HiveApiEndpoints.ForbiddenProblem("Device is not active.");
            }
        }
        else if (!ScopeAuthorizationService.CanViewSoftwareCatalogue(actor))
        {
            return HiveApiEndpoints.ForbiddenProblem("Caller is not authorized to download software packages.");
        }

        var package = await deploymentRepository.GetPackageByIdAsync(packageId, cancellationToken);
        if (package is null || !artifactStore.Exists(package.StoragePath))
        {
            return HiveApiEndpoints.Problem(
                StatusCodes.Status404NotFound,
                "Package not found",
                $"Package '{packageId}' was not found.");
        }

        var etag = $"\"{package.Sha256}\"";

        if (context.Request.Headers.TryGetValue(HeaderNames.IfNoneMatch, out var ifNoneMatchValues) &&
            EntityTagMatches(ifNoneMatchValues, package.Sha256))
        {
            context.Response.Headers.ETag = etag;
            context.Response.Headers.AcceptRanges = "bytes";
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        if (context.Request.Headers.TryGetValue(HeaderNames.Range, out var rangeValues) &&
            !string.IsNullOrWhiteSpace(rangeValues.ToString()))
        {
            if (!TryParseSingleByteRange(rangeValues.ToString(), package.SizeBytes, out var start, out var end))
            {
                context.Response.Headers.AcceptRanges = "bytes";
                context.Response.Headers.ETag = etag;
                context.Response.Headers.ContentRange = $"bytes */{package.SizeBytes}";
                return Results.StatusCode(StatusCodes.Status416RangeNotSatisfiable);
            }

            var length = end - start + 1;
            var rangeStream = artifactStore.OpenReadRange(package.StoragePath, start, length);
            return new PackageContentResult(
                rangeStream,
                StatusCodes.Status206PartialContent,
                length,
                etag,
                $"bytes {start}-{end}/{package.SizeBytes}");
        }

        var fullStream = artifactStore.OpenRead(package.StoragePath);
        return new PackageContentResult(
            fullStream,
            StatusCodes.Status200OK,
            package.SizeBytes,
            etag,
            null);
    }

    private static async Task<IResult> SignRelease(
        Guid releaseId,
        HttpContext context,
        ISoftwareRepository softwareRepository,
        IDeploymentRepository deploymentRepository,
        ReleaseSigningService signingService,
        IAuditRepository auditRepository,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var actor = context.User.ToActorIdentity();
        if (!ScopeAuthorizationService.CanSignSoftwareRelease(actor))
        {
            return HiveApiEndpoints.ForbiddenProblem("Caller is not authorized to sign software releases.");
        }

        SignReleaseRequest signRequest = new();
        if (context.Request.ContentLength != 0 && context.Request.HasJsonContentType())
        {
            try
            {
                signRequest = await context.Request.ReadFromJsonAsync<SignReleaseRequest>(cancellationToken)
                    ?? new SignReleaseRequest();
            }
            catch (JsonException)
            {
                return HiveApiEndpoints.ValidationProblem("Invalid JSON payload for release signing request.");
            }
        }

        if (signRequest.ValidDays is < 1 or > 3650)
        {
            return HiveApiEndpoints.ValidationProblem("ValidDays must be between 1 and 3650.");
        }

        var release = await softwareRepository.GetReleaseByIdAsync(releaseId, cancellationToken);
        if (release is null)
        {
            return HiveApiEndpoints.Problem(
                StatusCodes.Status404NotFound,
                "Software release not found",
                $"Software release '{releaseId}' was not found.");
        }

        if (release.ApprovalState == ReleaseApprovalState.Withdrawn)
        {
            return HiveApiEndpoints.Problem(
                StatusCodes.Status409Conflict,
                "Release withdrawn",
                "Withdrawn software releases cannot be signed.");
        }

        var product = await softwareRepository.GetProductByIdAsync(release.ProductId, cancellationToken);
        if (product is null)
        {
            return HiveApiEndpoints.Problem(
                StatusCodes.Status404NotFound,
                "Software product not found",
                $"Software product '{release.ProductId}' was not found.");
        }

        PackageArtifact? package = !string.IsNullOrWhiteSpace(release.PackageSha256)
            ? await deploymentRepository.GetPackageBySha256Async(release.PackageSha256, cancellationToken)
            : null;

        var installerType = package?.FileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true
            ? "EXE"
            : "MSI";
        var installProfile = string.Equals(installerType, "MSI", StringComparison.OrdinalIgnoreCase)
            ? "validated-msi-no-restart"
            : "validated-exe-silent";
        var silentArgs = string.Equals(installerType, "MSI", StringComparison.OrdinalIgnoreCase)
            ? "/qn /norestart"
            : "/S";

        var protectedProcesses = product.ProtectedProcessNames
            .Concat(signRequest.ProtectedProcesses ?? Array.Empty<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var nowUtc = timeProvider.GetUtcNow();
        var manifest = new SignedReleaseManifest(
            release.ReleaseId,
            product.ProductId,
            product.Name,
            release.VersionRaw,
            installerType,
            package?.Sha256 ?? release.PackageSha256 ?? new string('0', 64),
            package?.SizeBytes ?? 0L,
            package is not null
                ? $"{ProtocolConstants.ApiPrefix}/packages/{package.Id:D}/content"
                : $"{ProtocolConstants.ApiPrefix}/packages/{Guid.Empty:D}/content",
            silentArgs,
            "SoftwareInventory",
            product.MatchPattern,
            release.VersionRaw,
            protectedProcesses,
            signRequest.RebootAllowed,
            installProfile,
            nowUtc,
            nowUtc.AddDays(signRequest.ValidDays),
            signingService.KeyId);

        var envelope = signingService.SignManifest(manifest);

        await deploymentRepository.SaveSignedManifestAsync(
            release.ReleaseId,
            product.ProductId,
            envelope,
            actor.UserSid,
            nowUtc,
            cancellationToken);

        await auditRepository.AppendAsync(
            AuditEvent.Create(
                "User",
                actor.UserSid,
                "SoftwareReleaseSigned",
                "SoftwareRelease",
                release.ReleaseId.ToString("D"),
                "Success",
                null,
                CorrelationIdMiddleware.GetCorrelationId(context),
                nowUtc,
                JsonSerializer.Serialize(new
                {
                    productId = product.ProductId,
                    version = release.VersionRaw,
                    keyId = envelope.Manifest.KeyId,
                    packageSha256 = envelope.Manifest.PackageSha256
                })),
            cancellationToken);

        return Results.Ok(ToEnvelopeDto(envelope));
    }

    private static async Task<IResult> ListDeploymentJobs(
        string? siteId,
        HttpContext context,
        IDeploymentRepository deploymentRepository,
        IDeviceRepository deviceRepository,
        CancellationToken cancellationToken)
    {
        var actor = context.User.ToActorIdentity();
        var trimmedSiteId = string.IsNullOrWhiteSpace(siteId) ? null : siteId.Trim();

        if (trimmedSiteId is not null)
        {
            if (!ScopeAuthorizationService.CanViewDeployments(actor, trimmedSiteId))
            {
                return HiveApiEndpoints.ForbiddenProblem("Caller is not authorized to view deployments for the requested site.");
            }
        }
        else if (actor.IsDevice || !actor.HasAnyRole(
                     HiveRole.SoftwareAdmin,
                     HiveRole.SoftwareApprover,
                     HiveRole.Supervisor,
                     HiveRole.Administrator,
                     HiveRole.Auditor))
        {
            return HiveApiEndpoints.ForbiddenProblem("Caller is not authorized to view deployments.");
        }

        var jobs = await deploymentRepository.ListJobsAsync(
            trimmedSiteId,
            actor.AllowedSiteIds,
            actor.AllSitesAccess,
            cancellationToken);

        var devices = await deviceRepository.ListAsync(
            siteId: trimmedSiteId,
            allowedSiteIds: actor.AllowedSiteIds,
            allSites: actor.AllSitesAccess,
            cancellationToken: cancellationToken);
        var hostnames = devices.ToDictionary(d => d.DeviceId, d => d.Hostname);

        return Results.Ok(jobs.Select(j => ToJobDto(j, hostnames)).ToArray());
    }

    private static async Task<IResult> GetDeploymentJob(
        Guid jobId,
        HttpContext context,
        IDeploymentRepository deploymentRepository,
        IDeviceRepository deviceRepository,
        CancellationToken cancellationToken)
    {
        var jobWithTargets = await deploymentRepository.GetJobWithTargetsAsync(jobId, cancellationToken);
        if (jobWithTargets is null)
        {
            return HiveApiEndpoints.Problem(
                StatusCodes.Status404NotFound,
                "Deployment job not found",
                $"Deployment job '{jobId}' was not found.");
        }

        var actor = context.User.ToActorIdentity();
        if (!ScopeAuthorizationService.CanViewDeployments(actor, jobWithTargets.Job.SiteId))
        {
            return HiveApiEndpoints.ForbiddenProblem("Caller is not authorized to view deployments in this site.");
        }

        var devices = await deviceRepository.ListAsync(
            siteId: jobWithTargets.Job.SiteId,
            allSites: true,
            cancellationToken: cancellationToken);
        var hostnames = devices.ToDictionary(d => d.DeviceId, d => d.Hostname);

        return Results.Ok(ToJobDto(jobWithTargets, hostnames));
    }

    private static async Task<IResult> CreateDeploymentJob(
        CreateDeploymentJobRequest? request,
        HttpContext context,
        IDeviceRepository deviceRepository,
        ISoftwareRepository softwareRepository,
        IDeploymentRepository deploymentRepository,
        IAuditRepository auditRepository,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.SiteId))
        {
            return HiveApiEndpoints.ValidationProblem("SiteId is required.");
        }

        var siteId = request.SiteId.Trim();
        var actor = context.User.ToActorIdentity();
        if (!ScopeAuthorizationService.CanManageDeployments(actor, siteId))
        {
            return HiveApiEndpoints.ForbiddenProblem("Caller is not authorized to manage deployments for this site.");
        }

        if (!request.Confirmed)
        {
            return HiveApiEndpoints.ValidationProblem("Deployment creation must be explicitly confirmed (Confirmed=true).");
        }

        if (!Enum.TryParse<DeploymentPolicy>(request.Policy?.Trim(), ignoreCase: true, out var policy) ||
            !Enum.IsDefined(policy))
        {
            return HiveApiEndpoints.ValidationProblem("Policy must be one of Mandatory, Optional, NotifyOnly, or RingPilot.");
        }

        if (request.TargetDeviceIds is null || request.TargetDeviceIds.Count == 0 ||
            request.TargetDeviceIds.Any(id => id == Guid.Empty))
        {
            return HiveApiEndpoints.ValidationProblem("At least one valid target device ID is required.");
        }

        var distinctDeviceIds = request.TargetDeviceIds.Distinct().ToArray();

        var release = await softwareRepository.GetReleaseByIdAsync(request.ReleaseId, cancellationToken);
        if (release is null)
        {
            return HiveApiEndpoints.Problem(
                StatusCodes.Status404NotFound,
                "Software release not found",
                $"Software release '{request.ReleaseId}' was not found.");
        }

        if (release.ApprovalState != ReleaseApprovalState.Approved)
        {
            return HiveApiEndpoints.Problem(
                StatusCodes.Status409Conflict,
                "Release not approved",
                $"Software release '{release.ReleaseId}' is '{release.ApprovalState}' and must be Approved before deployment.");
        }

        var nowUtc = timeProvider.GetUtcNow();
        var envelope = await deploymentRepository.GetSignedManifestAsync(release.ReleaseId, cancellationToken);
        if (envelope is null)
        {
            return HiveApiEndpoints.Problem(
                StatusCodes.Status409Conflict,
                "Release not signed",
                $"Software release '{release.ReleaseId}' does not have a signed deployment manifest.");
        }

        if (!ReleaseManifestCrypto.Verify(envelope, out var verifyReason, utcNow: nowUtc))
        {
            return HiveApiEndpoints.Problem(
                StatusCodes.Status409Conflict,
                "Signed manifest invalid",
                $"Signed manifest for release '{release.ReleaseId}' failed verification ({verifyReason}).");
        }

        var targetDevices = new List<Device>(distinctDeviceIds.Length);
        foreach (var deviceId in distinctDeviceIds)
        {
            var device = await deviceRepository.GetByIdAsync(deviceId, cancellationToken);
            if (device is null)
            {
                return HiveApiEndpoints.ValidationProblem($"Target device '{deviceId}' was not found.");
            }

            if (!actor.CanAccessSite(device.SiteId))
            {
                return HiveApiEndpoints.ForbiddenProblem($"Caller is not authorized to deploy to device '{deviceId}' in site '{device.SiteId}'.");
            }

            if (!string.Equals(device.SiteId, siteId, StringComparison.OrdinalIgnoreCase))
            {
                return HiveApiEndpoints.ValidationProblem(
                    $"Target device '{deviceId}' belongs to site '{device.SiteId}', not '{siteId}'.");
            }

            if (device.EnrollmentState != EnrollmentState.Active)
            {
                return HiveApiEndpoints.Problem(
                    StatusCodes.Status409Conflict,
                    "Target device not active",
                    $"Target device '{deviceId}' is in enrollment state '{device.EnrollmentState}'.");
            }

            targetDevices.Add(device);
        }

        var product = await softwareRepository.GetProductByIdAsync(release.ProductId, cancellationToken);
        var jobId = Guid.NewGuid();
        var job = new DeploymentJob(
            jobId,
            siteId,
            release.ReleaseId,
            product?.Name ?? envelope.Manifest.ProductName,
            release.VersionRaw,
            policy,
            actor.UserSid,
            nowUtc);

        var targets = targetDevices
            .Select(d => new DeploymentTarget(
                Guid.NewGuid(),
                jobId,
                d.DeviceId,
                DeploymentState.Pending,
                AttemptCount: 0,
                ActiveLeaseId: null,
                LeaseExpiresUtc: null,
                LastReasonCode: "deployment_queued",
                LastMessage: "Queued for agent synchronization.",
                DeferredUntilUtc: null,
                UpdatedUtc: nowUtc))
            .ToArray();

        await deploymentRepository.CreateJobAsync(job, targets, cancellationToken);

        await auditRepository.AppendAsync(
            AuditEvent.Create(
                "User",
                actor.UserSid,
                "DeploymentJobCreated",
                "DeploymentJob",
                job.Id.ToString("D"),
                "Success",
                job.SiteId,
                CorrelationIdMiddleware.GetCorrelationId(context),
                nowUtc,
                JsonSerializer.Serialize(new
                {
                    releaseId = job.ReleaseId,
                    policy = job.Policy.ToString(),
                    targetCount = targets.Length
                })),
            cancellationToken);

        var hostnames = targetDevices.ToDictionary(d => d.DeviceId, d => d.Hostname);
        var dto = ToJobDto(new DeploymentJobWithTargets(job, targets), hostnames);
        return Results.Created($"{ProtocolConstants.ApiPrefix}/deployments/{job.Id:D}", dto);
    }

    private static async Task<IResult> CancelDeploymentJob(
        Guid jobId,
        HttpContext context,
        IDeploymentRepository deploymentRepository,
        IDeviceRepository deviceRepository,
        IAuditRepository auditRepository,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var existing = await deploymentRepository.GetJobWithTargetsAsync(jobId, cancellationToken);
        if (existing is null)
        {
            return HiveApiEndpoints.Problem(
                StatusCodes.Status404NotFound,
                "Deployment job not found",
                $"Deployment job '{jobId}' was not found.");
        }

        var actor = context.User.ToActorIdentity();
        if (!ScopeAuthorizationService.CanManageDeployments(actor, existing.Job.SiteId))
        {
            return HiveApiEndpoints.ForbiddenProblem("Caller is not authorized to cancel deployments in this site.");
        }

        var nowUtc = timeProvider.GetUtcNow();
        var updated = await deploymentRepository.CancelJobTargetsAsync(jobId, nowUtc, cancellationToken);
        if (updated is null)
        {
            return HiveApiEndpoints.Problem(
                StatusCodes.Status404NotFound,
                "Deployment job not found",
                $"Deployment job '{jobId}' was not found.");
        }

        await auditRepository.AppendAsync(
            AuditEvent.Create(
                "User",
                actor.UserSid,
                "DeploymentJobCancelled",
                "DeploymentJob",
                updated.Job.Id.ToString("D"),
                "Success",
                updated.Job.SiteId,
                CorrelationIdMiddleware.GetCorrelationId(context),
                nowUtc,
                JsonSerializer.Serialize(new
                {
                    cancelledCount = updated.Targets.Count(t => t.State == DeploymentState.Cancelled)
                })),
            cancellationToken);

        var devices = await deviceRepository.ListAsync(
            siteId: updated.Job.SiteId,
            allSites: true,
            cancellationToken: cancellationToken);
        var hostnames = devices.ToDictionary(d => d.DeviceId, d => d.Hostname);

        return Results.Ok(ToJobDto(updated, hostnames));
    }

    private static async Task<IResult> GetAgentSync(
        HttpContext context,
        IDeviceRepository deviceRepository,
        IDeploymentRepository deploymentRepository,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var actor = context.User.ToActorIdentity();
        if (!actor.IsDevice || actor.DeviceId is not Guid deviceId || !ScopeAuthorizationService.CanSendHeartbeat(actor, deviceId))
        {
            return HiveApiEndpoints.ForbiddenProblem("Valid device certificate authentication is required.");
        }

        var device = await deviceRepository.GetByIdAsync(deviceId, cancellationToken);
        if (device is null || device.EnrollmentState != EnrollmentState.Active)
        {
            return HiveApiEndpoints.ForbiddenProblem("Device is not active.");
        }

        var active = await deploymentRepository.ListActiveDeploymentsForDeviceAsync(deviceId, cancellationToken);
        var items = active
            .Select(item => new AgentPendingDeploymentDto(
                item.Target.Id,
                item.Job.Id,
                item.Job.ReleaseId,
                item.Job.Policy.ToString(),
                item.Target.State.ToString(),
                ToEnvelopeDto(item.Envelope)))
            .ToArray();

        return Results.Ok(new AgentSyncResponse(timeProvider.GetUtcNow(), items));
    }

    private static async Task<IResult> AcquireDeploymentLease(
        Guid targetId,
        HttpContext context,
        IDeviceRepository deviceRepository,
        IDeploymentRepository deploymentRepository,
        DeploymentControlState controlState,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var actor = context.User.ToActorIdentity();
        if (!actor.IsDevice || actor.DeviceId is not Guid deviceId || !ScopeAuthorizationService.CanSendHeartbeat(actor, deviceId))
        {
            return HiveApiEndpoints.ForbiddenProblem("Valid device certificate authentication is required.");
        }

        var device = await deviceRepository.GetByIdAsync(deviceId, cancellationToken);
        if (device is null || device.EnrollmentState != EnrollmentState.Active)
        {
            return HiveApiEndpoints.ForbiddenProblem("Device is not active.");
        }

        var target = await deploymentRepository.GetTargetByIdAsync(targetId, cancellationToken);
        if (target is null)
        {
            return HiveApiEndpoints.Problem(
                StatusCodes.Status404NotFound,
                "Deployment target not found",
                $"Deployment target '{targetId}' was not found.");
        }

        if (target.DeviceId != deviceId)
        {
            return HiveApiEndpoints.ForbiddenProblem("Deployment target belongs to another device.");
        }

        var control = controlState.GetSnapshot();
        if (control.DeploymentsPaused)
        {
            var reasonDetail = string.IsNullOrWhiteSpace(control.PauseReason)
                ? "Deployments are paused due to an active emergency stop."
                : $"Deployments are paused due to an active emergency stop ({control.PauseReason}).";
            return Results.Ok(new DeploymentLeaseResponse(
                false,
                targetId,
                null,
                null,
                "deployments_paused_emergency_stop",
                reasonDetail));
        }

        IReadOnlyCollection<string>? protectedProcesses = null;
        var jobWithTargets = await deploymentRepository.GetJobWithTargetsAsync(target.JobId, cancellationToken);
        if (jobWithTargets is not null)
        {
            var envelope = await deploymentRepository.GetSignedManifestAsync(jobWithTargets.Job.ReleaseId, cancellationToken);
            protectedProcesses = envelope?.Manifest.ProtectedProcesses;
        }

        var result = await deploymentRepository.TryAcquireLeaseAsync(
            targetId,
            deviceId,
            timeProvider.GetUtcNow(),
            DefaultLeaseDuration,
            protectedProcesses,
            cancellationToken);

        return result.Status switch
        {
            LeaseAcquisitionStatus.NotFound => HiveApiEndpoints.Problem(
                StatusCodes.Status404NotFound,
                "Deployment target not found",
                $"Deployment target '{targetId}' was not found."),
            LeaseAcquisitionStatus.WrongDevice => HiveApiEndpoints.ForbiddenProblem(
                "Deployment target belongs to another device."),
            _ => Results.Ok(result.Response)
        };
    }

    private static async Task<IResult> RecordDeploymentEvent(
        Guid targetId,
        DeploymentEventRequest? request,
        HttpContext context,
        IDeviceRepository deviceRepository,
        IDeploymentRepository deploymentRepository,
        IAuditRepository auditRepository,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var actor = context.User.ToActorIdentity();
        if (!actor.IsDevice || actor.DeviceId is not Guid deviceId || !ScopeAuthorizationService.CanSendHeartbeat(actor, deviceId))
        {
            return HiveApiEndpoints.ForbiddenProblem("Valid device certificate authentication is required.");
        }

        var device = await deviceRepository.GetByIdAsync(deviceId, cancellationToken);
        if (device is null || device.EnrollmentState != EnrollmentState.Active)
        {
            return HiveApiEndpoints.ForbiddenProblem("Device is not active.");
        }

        if (request is null ||
            !Enum.TryParse<DeploymentState>(request.State?.Trim(), ignoreCase: true, out var nextState) ||
            !Enum.IsDefined(nextState))
        {
            return HiveApiEndpoints.ValidationProblem("A valid DeploymentState is required.");
        }

        if (string.IsNullOrWhiteSpace(request.ReasonCode))
        {
            return HiveApiEndpoints.ValidationProblem("ReasonCode is required.");
        }

        var reasonCode = BoundString(request.ReasonCode.Trim(), MaxReasonLength);
        var message = string.IsNullOrWhiteSpace(request.Message)
            ? null
            : BoundString(request.Message.Trim(), MaxMessageLength);

        var nowUtc = timeProvider.GetUtcNow();
        var result = await deploymentRepository.RecordTargetEventAsync(
            targetId,
            deviceId,
            nextState,
            reasonCode,
            message,
            request.LeaseId,
            request.ExitCode,
            request.DeferredUntilUtc,
            nowUtc,
            cancellationToken);

        if (result.Status == RecordDeploymentEventStatus.NotFound)
        {
            return HiveApiEndpoints.Problem(
                StatusCodes.Status404NotFound,
                "Deployment target not found",
                $"Deployment target '{targetId}' was not found.");
        }

        if (result.Status == RecordDeploymentEventStatus.WrongDevice)
        {
            return HiveApiEndpoints.ForbiddenProblem("Deployment target belongs to another device.");
        }

        if (result.Status == RecordDeploymentEventStatus.IllegalTransition)
        {
            return HiveApiEndpoints.Problem(
                StatusCodes.Status409Conflict,
                "Illegal deployment state transition",
                result.ErrorDetail);
        }

        if (result.Status == RecordDeploymentEventStatus.LeaseRequiredOrInvalid)
        {
            return HiveApiEndpoints.Problem(
                StatusCodes.Status409Conflict,
                "Deployment lease invalid or expired",
                result.ErrorDetail);
        }

        var updated = result.Target!;

        await auditRepository.AppendAsync(
            AuditEvent.Create(
                "Device",
                deviceId.ToString("D"),
                "DeploymentTargetEventRecorded",
                "DeploymentTarget",
                updated.Id.ToString("D"),
                "Success",
                device.SiteId,
                CorrelationIdMiddleware.GetCorrelationId(context),
                nowUtc,
                JsonSerializer.Serialize(new
                {
                    jobId = updated.JobId,
                    state = updated.State.ToString(),
                    attemptCount = updated.AttemptCount,
                    reasonCode = updated.LastReasonCode,
                    exitCode = request.ExitCode
                })),
            cancellationToken);

        return Results.Ok(ToTargetDto(updated, device.Hostname));
    }

    private static bool EntityTagMatches(Microsoft.Extensions.Primitives.StringValues headerValues, string expectedSha256)
    {
        if (!EntityTagHeaderValue.TryParseList(headerValues, out var parsedTags))
        {
            return false;
        }

        foreach (var tag in parsedTags)
        {
            if (tag.Equals(EntityTagHeaderValue.Any))
            {
                return true;
            }

            var unquoted = tag.Tag.Value?.Trim('"');
            if (string.Equals(unquoted, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryParseSingleByteRange(string rawHeader, long totalLength, out long start, out long end)
    {
        start = 0;
        end = 0;

        if (totalLength <= 0 ||
            !RangeHeaderValue.TryParse(rawHeader, out var rangeHeader) ||
            !string.Equals(rangeHeader.Unit.Value, "bytes", StringComparison.OrdinalIgnoreCase) ||
            rangeHeader.Ranges.Count != 1)
        {
            return false;
        }

        var item = rangeHeader.Ranges.First();
        if (item.From.HasValue && item.To.HasValue)
        {
            start = item.From.Value;
            if (start < 0 || start > item.To.Value || start >= totalLength)
            {
                return false;
            }

            end = Math.Min(item.To.Value, totalLength - 1);
            return true;
        }

        if (item.From.HasValue)
        {
            start = item.From.Value;
            if (start < 0 || start >= totalLength)
            {
                return false;
            }

            end = totalLength - 1;
            return true;
        }

        if (item.To.HasValue)
        {
            var suffixLength = item.To.Value;
            if (suffixLength <= 0)
            {
                return false;
            }

            start = Math.Max(0, totalLength - suffixLength);
            end = totalLength - 1;
            return true;
        }

        return false;
    }

    private static string BoundString(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private static SignedManifestEnvelopeDto ToEnvelopeDto(SignedManifestEnvelope envelope) =>
        new(
            new SignedReleaseManifestDto(
                envelope.Manifest.ReleaseId,
                envelope.Manifest.ProductId,
                envelope.Manifest.ProductName,
                envelope.Manifest.Version,
                envelope.Manifest.InstallerType,
                envelope.Manifest.PackageSha256,
                envelope.Manifest.PackageSizeBytes,
                envelope.Manifest.DownloadRelativeUrl,
                envelope.Manifest.SilentArgs,
                envelope.Manifest.VerificationType,
                envelope.Manifest.VerificationTarget,
                envelope.Manifest.ExpectedVersion,
                envelope.Manifest.ProtectedProcesses,
                envelope.Manifest.RebootAllowed,
                envelope.Manifest.InstallProfile,
                envelope.Manifest.IssuedUtc,
                envelope.Manifest.ExpiresUtc,
                envelope.Manifest.KeyId),
            envelope.ManifestCanonicalJson,
            envelope.SignatureBase64,
            envelope.Algorithm,
            envelope.PublicKeyPem);

    private static DeploymentTargetDto ToTargetDto(DeploymentTarget target, string hostname) =>
        new(
            target.Id,
            target.JobId,
            target.DeviceId,
            hostname,
            target.State.ToString(),
            target.AttemptCount,
            target.LastReasonCode,
            target.LastMessage,
            target.DeferredUntilUtc,
            target.UpdatedUtc);

    private static DeploymentJobDto ToJobDto(
        DeploymentJobWithTargets jobWithTargets,
        IReadOnlyDictionary<Guid, string> hostnames) =>
        new(
            jobWithTargets.Job.Id,
            jobWithTargets.Job.SiteId,
            jobWithTargets.Job.ReleaseId,
            jobWithTargets.Job.ProductName,
            jobWithTargets.Job.Version,
            jobWithTargets.Job.Policy.ToString(),
            jobWithTargets.Job.CreatedBySid,
            jobWithTargets.Job.CreatedUtc,
            jobWithTargets.Targets
                .Select(t => ToTargetDto(t, hostnames.GetValueOrDefault(t.DeviceId, "unknown")))
                .ToArray());

    private sealed class PackageContentResult : IResult
    {
        private readonly Stream _stream;
        private readonly int _statusCode;
        private readonly long _contentLength;
        private readonly string _etag;
        private readonly string? _contentRange;

        public PackageContentResult(
            Stream stream,
            int statusCode,
            long contentLength,
            string etag,
            string? contentRange)
        {
            _stream = stream;
            _statusCode = statusCode;
            _contentLength = contentLength;
            _etag = etag;
            _contentRange = contentRange;
        }

        public async Task ExecuteAsync(HttpContext httpContext)
        {
            await using (_stream)
            {
                httpContext.Response.StatusCode = _statusCode;
                httpContext.Response.Headers.AcceptRanges = "bytes";
                httpContext.Response.Headers.ETag = _etag;
                if (!string.IsNullOrEmpty(_contentRange))
                {
                    httpContext.Response.Headers.ContentRange = _contentRange;
                }

                httpContext.Response.ContentType = "application/octet-stream";
                httpContext.Response.ContentLength = _contentLength;

                if (!HttpMethods.IsHead(httpContext.Request.Method))
                {
                    await _stream.CopyToAsync(httpContext.Response.Body, httpContext.RequestAborted);
                }
            }
        }
    }
}
