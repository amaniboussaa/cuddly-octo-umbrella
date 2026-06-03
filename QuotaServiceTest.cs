using System;
using System.Threading.Tasks;
using Xunit;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Ddata.Infrastructure.Data;
using Ddata.Infrastructure.Data.Quota;
using Ddata.Domain.Entities;
using Ddata.Domain.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ddata.Test.Services;

public class QuotaServiceTest
{
    private readonly ApplicationDatabaseContext _db;
    private readonly IQuotaCounterStore _counterStore;
    private readonly IQuotaService _quotaService;
    private readonly Ddata.Infrastructure.Configuration.QuotaOptions _opts;

    public QuotaServiceTest()
    {
        var options = new DbContextOptionsBuilder<ApplicationDatabaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new ApplicationDatabaseContext(options, new FakeHttpContextAccessor());
        _counterStore = new PostgresQuotaCounterStore(_db);
        _opts = new Ddata.Infrastructure.Configuration.QuotaOptions
        {
            UseRequestLimit = true,
            UseVolumeLimit = true
        };
        var optionsWrapper = Microsoft.Extensions.Options.Options.Create(_opts);
        var logger = NullLogger<QuotaService>.Instance;
        _quotaService = new QuotaService(_db, _counterStore, optionsWrapper, logger);
    }

    [Fact]
    public async Task Increment_ShouldAccumulateCounts()
    {
        var plan = new QuotaPlan { Name = "basic", DailyRequestLimit = 5, DailyVolumeLimitBytes = 1000 };
        _db.QuotaPlans.Add(plan);
        _db.UserQuotaAssignments.Add(new UserQuotaAssignment { UserId = "user1", QuotaPlanId = plan.Id, EffectiveFrom = DateTime.UtcNow.Date });
        await _db.SaveChangesAsync();

        var r1 = await _quotaService.IncrementAndEvaluateAsync("user1", 1, 100);
        r1.Usage.RequestsCount.Should().Be(1);
        r1.Usage.VolumeBytes.Should().Be(100);
        r1.OverRequestLimit.Should().BeFalse();
        r1.OverVolumeLimit.Should().BeFalse();

        var r2 = await _quotaService.IncrementAndEvaluateAsync("user1", 1, 500);
        r2.Usage.RequestsCount.Should().Be(2);
        r2.Usage.VolumeBytes.Should().Be(600);
        r2.OverRequestLimit.Should().BeFalse();
        r2.OverVolumeLimit.Should().BeFalse();
    }

    [Fact]
    public async Task RequestOnly_ShouldIgnoreVolumeLimit()
    {
        _opts.UseRequestLimit = true; _opts.UseVolumeLimit = false;
        var plan = new QuotaPlan { Name = "reqOnly", DailyRequestLimit = 2, DailyVolumeLimitBytes = 50 };
        _db.QuotaPlans.Add(plan);
        _db.UserQuotaAssignments.Add(new UserQuotaAssignment { UserId = "u_req", QuotaPlanId = plan.Id, EffectiveFrom = DateTime.UtcNow.Date });
        await _db.SaveChangesAsync();

        await _quotaService.IncrementAndEvaluateAsync("u_req", 1, 40); // 1 / 40
        var r2 = await _quotaService.IncrementAndEvaluateAsync("u_req", 1, 60); // 2 / 100 volume would exceed but disabled
        r2.OverRequestLimit.Should().BeFalse();
        r2.OverVolumeLimit.Should().BeFalse(); // disabled dimension
        var r3 = await _quotaService.IncrementAndEvaluateAsync("u_req", 1, 10); // 3 / 110 -> request limit exceeded
        r3.OverRequestLimit.Should().BeTrue();
        r3.OverVolumeLimit.Should().BeFalse();
    }

    [Fact]
    public async Task VolumeOnly_ShouldIgnoreRequestLimit()
    {
        _opts.UseRequestLimit = false; _opts.UseVolumeLimit = true;
        var plan = new QuotaPlan { Name = "volOnly", DailyRequestLimit = 1, DailyVolumeLimitBytes = 120 };
        _db.QuotaPlans.Add(plan);
        _db.UserQuotaAssignments.Add(new UserQuotaAssignment { UserId = "u_vol", QuotaPlanId = plan.Id, EffectiveFrom = DateTime.UtcNow.Date });
        await _db.SaveChangesAsync();

        await _quotaService.IncrementAndEvaluateAsync("u_vol", 1, 50); // requests=1 volume=50
        var r2 = await _quotaService.IncrementAndEvaluateAsync("u_vol", 1, 80); // requests=2 volume=130 -> volume exceeded only
        r2.OverRequestLimit.Should().BeFalse(); // disabled dimension
        r2.OverVolumeLimit.Should().BeTrue();
    }

    [Fact]
    public async Task NeitherEnabled_ShouldNeverShowOver()
    {
        _opts.UseRequestLimit = false; _opts.UseVolumeLimit = false;
        var plan = new QuotaPlan { Name = "none", DailyRequestLimit = 1, DailyVolumeLimitBytes = 10 };
        _db.QuotaPlans.Add(plan);
        _db.UserQuotaAssignments.Add(new UserQuotaAssignment { UserId = "u_none", QuotaPlanId = plan.Id, EffectiveFrom = DateTime.UtcNow.Date });
        await _db.SaveChangesAsync();

        for (int i = 0; i < 5; i++)
        {
            var r = await _quotaService.IncrementAndEvaluateAsync("u_none", 1, 1000);
            r.OverRequestLimit.Should().BeFalse();
            r.OverVolumeLimit.Should().BeFalse();
        }
    }

    [Fact]
    public async Task Increment_ShouldFlagOverLimits()
    {
        var plan = new QuotaPlan { Name = "tiny", DailyRequestLimit = 2, DailyVolumeLimitBytes = 200 };
        _db.QuotaPlans.Add(plan);
        _db.UserQuotaAssignments.Add(new UserQuotaAssignment { UserId = "user2", QuotaPlanId = plan.Id, EffectiveFrom = DateTime.UtcNow.Date });
        await _db.SaveChangesAsync();

        await _quotaService.IncrementAndEvaluateAsync("user2", 1, 100); // 1 / 100
        var r2 = await _quotaService.IncrementAndEvaluateAsync("user2", 1, 150); // 2 / 250 -> volume exceeded
        r2.OverRequestLimit.Should().BeFalse(); // exactly at request limit not over
        r2.OverVolumeLimit.Should().BeTrue();
        var r3 = await _quotaService.IncrementAndEvaluateAsync("user2", 1, 10); // 3 / 260 -> both over
        r3.OverRequestLimit.Should().BeTrue();
        r3.OverVolumeLimit.Should().BeTrue();
    }

    [Fact]
    public async Task NoAssignment_FallsBackToDefaultPlan()
    {
        // Seed the default plan matching QuotaOptions.DefaultPlanName = "standard"
        var defaultPlan = new QuotaPlan { Name = "standard", DailyRequestLimit = 1000, DailyVolumeLimitBytes = 100_000_000 };
        _db.QuotaPlans.Add(defaultPlan);
        await _db.SaveChangesAsync();

        // User has NO UserQuotaAssignment — should fall back to default plan
        var r = await _quotaService.IncrementAndEvaluateAsync("new_user", 1, 100);
        r.Usage.RequestsCount.Should().Be(1);
        r.Usage.VolumeBytes.Should().Be(100);
        r.Limits.RequestLimit.Should().Be(1000);
        r.Limits.VolumeLimitBytes.Should().Be(100_000_000);
        r.OverRequestLimit.Should().BeFalse();
        r.OverVolumeLimit.Should().BeFalse();
    }

    [Fact]
    public async Task NoAssignmentAndNoDefaultPlan_AutoCreatesIt()
    {
        // No plan named "standard" exists — fallback should auto-create it
        var r = await _quotaService.IncrementAndEvaluateAsync("orphan", 10, 50000);
        r.Limits.RequestLimit.Should().Be(1000);
        r.Limits.VolumeLimitBytes.Should().Be(100_000_000);
        r.OverRequestLimit.Should().BeFalse();
        r.OverVolumeLimit.Should().BeFalse();

        // Verify the plan was persisted
        var saved = await _db.QuotaPlans.FirstOrDefaultAsync(p => p.Name == "standard");
        saved.Should().NotBeNull();
        saved.DailyRequestLimit.Should().Be(1000);
        saved.DailyVolumeLimitBytes.Should().Be(100_000_000);
    }

    private class FakeHttpContextAccessor : Microsoft.AspNetCore.Http.IHttpContextAccessor
    {
        public Microsoft.AspNetCore.Http.HttpContext HttpContext { get; set; }
    }
}
