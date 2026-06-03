using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Ddata.Domain.Entities;
using Ddata.Domain.Services;
using Ddata.Infrastructure.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Ddata.Infrastructure.Services;

namespace Ddata.Infrastructure.Data.Quota;

public class QuotaStatus
{
    public string UserId { get; set; }
    public QuotaPlan? QuotaPlan { get; set; }
    public int RequestsUsed { get; set; }
    public long VolumeUsed { get; set; }
    public bool OverRequestLimit { get; set; }
    public bool OverVolumeLimit { get; set; }

    public QuotaStatus(string userId, QuotaPlan? quotaPlan, int requestsUsed, long volumeUsed, bool overRequestLimit = false, bool overVolumeLimit = false)
    {
        UserId = userId;
        QuotaPlan = quotaPlan;
        RequestsUsed = requestsUsed;
        VolumeUsed = volumeUsed;
        OverRequestLimit = overRequestLimit;
        OverVolumeLimit = overVolumeLimit;
    }
}

public class QuotaService : IQuotaService
{
    private readonly ApplicationDatabaseContext _db;
    private readonly IQuotaCounterStore _counterStore;
    private readonly QuotaOptions _options;
    private readonly ILogger<QuotaService> _logger;
    private readonly IQuotaCacheService _cacheService;

    public QuotaService(
        ApplicationDatabaseContext db, 
        IQuotaCounterStore counterStore, 
        IOptions<QuotaOptions> options, 
        ILogger<QuotaService> logger,
        IQuotaCacheService cacheService = null)
    {
        _db = db;
        _counterStore = counterStore;
        _options = options?.Value ?? new QuotaOptions();
        _logger = logger;
        _cacheService = cacheService;
    }

    private async Task<QuotaLimits> ResolveLimitsAsync(string userId, CancellationToken ct)
    {
        // Try to get from cache first
        if (_cacheService != null)
        {
            var cachedAssignment = await _cacheService.GetAssignmentAsync(userId, ct);
            if (cachedAssignment != null && cachedAssignment.QuotaPlan != null)
            {
                _logger.LogDebug("Using cached quota assignment for user {UserId}", userId);
                return new QuotaLimits(cachedAssignment.QuotaPlan.DailyRequestLimit, cachedAssignment.QuotaPlan.DailyVolumeLimitBytes);
            }
        }

        // If not in cache or cache disabled, query database
        var today = DateTime.UtcNow.Date;
        var assignment = await _db.UserQuotaAssignments
            .Include(a => a.QuotaPlan)
            .Where(a => a.UserId == userId && a.IsActive && a.EffectiveFrom <= today && (a.EffectiveTo == null || a.EffectiveTo >= today))
            .OrderByDescending(a => a.EffectiveFrom)
            .FirstOrDefaultAsync(ct);

        if (assignment?.QuotaPlan != null)
        {
            // Cache the assignment for future requests
            if (_cacheService != null)
            {
                await _cacheService.SetAssignmentAsync(assignment, ct);
            }
            return new QuotaLimits(assignment.QuotaPlan.DailyRequestLimit, assignment.QuotaPlan.DailyVolumeLimitBytes);
        }

        // Fall back to default plan when no explicit assignment exists
        if (!string.IsNullOrEmpty(_options.DefaultPlanName))
        {
            var defaultPlan = await GetOrCreateDefaultPlanAsync(ct);
            if (defaultPlan != null)
            {
                _logger.LogDebug("Using default plan '{PlanName}' for user {UserId}", _options.DefaultPlanName, userId);
                return new QuotaLimits(defaultPlan.DailyRequestLimit, defaultPlan.DailyVolumeLimitBytes);
            }
        }

        return new QuotaLimits(null, null); // unlimited
    }

    public async Task<QuotaEvaluationResult> IncrementAndEvaluateAsync(string userId, int requestDelta, long volumeDeltaBytes, CancellationToken cancellationToken = default)
    {
        var limits = await ResolveLimitsAsync(userId, cancellationToken);
        // Flags may disable specific limits (treated as unlimited)
        var effectiveRequestLimit = _options?.UseRequestLimit == false ? null : limits.RequestLimit;
        var effectiveVolumeLimit = _options?.UseVolumeLimit == false ? null : limits.VolumeLimitBytes;
        var result = await _counterStore.IncrementAsync(userId, DateTime.UtcNow, requestDelta, volumeDeltaBytes, effectiveRequestLimit, effectiveVolumeLimit, cancellationToken);
        var usage = new QuotaUsage(result.RequestsCount, result.VolumeBytes);
        // Over flags only meaningful if that limit type is enabled
        var overReq = (_options?.UseRequestLimit ?? true) && result.ExceededRequestLimit;
        var overVol = (_options?.UseVolumeLimit ?? true) && result.ExceededVolumeLimit;
        return new QuotaEvaluationResult(usage, limits, overReq, overVol);
    }

    public async Task<QuotaEvaluationResult> GetCurrentAsync(string userId, CancellationToken cancellationToken = default)
    {
        var limits = await ResolveLimitsAsync(userId, cancellationToken);
        var (req, vol) = await _counterStore.GetUsageAsync(userId, DateTime.UtcNow, cancellationToken);
        var overReq = (_options?.UseRequestLimit ?? true) && limits.RequestLimit.HasValue && req > limits.RequestLimit.Value;
        var overVol = (_options?.UseVolumeLimit ?? true) && limits.VolumeLimitBytes.HasValue && vol > limits.VolumeLimitBytes.Value;
        return new QuotaEvaluationResult(new QuotaUsage(req, vol), limits, overReq, overVol);
    }

    public async Task<QuotaStatus> GetCurrentQuotaStatusAsync(string userId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("QuotaService: Getting quota status for user {UserId}", userId);

        var effectivePlan = await GetEffectivePlanAsync(userId, cancellationToken);
        
        if (effectivePlan == null)
        {
            _logger.LogWarning("QuotaService: No quota plan assigned to user {UserId}, using unlimited", userId);
            return new QuotaStatus(userId, null, 0, 0);
        }
        else
        {
            _logger.LogInformation("QuotaService: User {UserId} assigned to plan '{PlanName}' (Requests: {Req}, Volume: {Vol} bytes)", 
                userId, effectivePlan.Name, 
                effectivePlan.DailyRequestLimit?.ToString() ?? "unlimited",
                effectivePlan.DailyVolumeLimitBytes?.ToString() ?? "unlimited");
        }

        var (requests, volume) = await _counterStore.GetUsageAsync(userId, DateTime.UtcNow, cancellationToken);
        var overReq = (_options?.UseRequestLimit ?? true) && effectivePlan.DailyRequestLimit.HasValue && requests > effectivePlan.DailyRequestLimit.Value;
        var overVol = (_options?.UseVolumeLimit ?? true) && effectivePlan.DailyVolumeLimitBytes.HasValue && volume > effectivePlan.DailyVolumeLimitBytes.Value;

        _logger.LogDebug("QuotaService: Current usage for {UserId} - Requests: {Req}, Volume: {Vol} bytes", 
            userId, requests, volume);

        return new QuotaStatus(userId, effectivePlan, requests, volume, overReq, overVol);
    }

    private async Task<QuotaPlan?> GetEffectivePlanAsync(string userId, CancellationToken cancellationToken)
    {
        _logger.LogDebug("QuotaService: Resolving effective plan for user {UserId}", userId);
        
        // Simplified: pick the latest active assignment whose EffectiveFrom <= today and (EffectiveTo null or >= today)
        var today = DateTime.UtcNow.Date;
        var assignment = await _db.UserQuotaAssignments
            .Include(a => a.QuotaPlan)
            .Where(a => a.UserId == userId && a.IsActive && a.EffectiveFrom <= today && (a.EffectiveTo == null || a.EffectiveTo >= today))
            .OrderByDescending(a => a.EffectiveFrom)
            .FirstOrDefaultAsync(cancellationToken);

        if (assignment == null)
        {
            _logger.LogWarning("QuotaService: No active assignment found for user {UserId}", userId);
            // Fall back to default plan
            if (!string.IsNullOrEmpty(_options.DefaultPlanName))
            {
                var defaultPlan = await GetOrCreateDefaultPlanAsync(cancellationToken);
                if (defaultPlan != null)
                {
                    _logger.LogInformation("QuotaService: Using default plan '{PlanName}' for user {UserId}", _options.DefaultPlanName, userId);
                    return defaultPlan;
                }
            }
            return null;
        }

        _logger.LogInformation("QuotaService: Found active assignment for user {UserId} to plan {PlanId}", 
            userId, assignment.QuotaPlanId);

        return assignment.QuotaPlan;
    }

    private async Task<QuotaPlan?> GetOrCreateDefaultPlanAsync(CancellationToken ct)
    {
        var plan = await _db.QuotaPlans.FirstOrDefaultAsync(p => p.Name == _options.DefaultPlanName && p.IsActive, ct);
        if (plan != null) return plan;

        plan = new QuotaPlan
        {
            Name = _options.DefaultPlanName,
            DailyRequestLimit = 1_000,
            DailyVolumeLimitBytes = 100_000_000,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.QuotaPlans.Add(plan);
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Created default quota plan '{PlanName}' (1000 requests/day, 100 MB/day)", _options.DefaultPlanName);
        return plan;
    }
}
