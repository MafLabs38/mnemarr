using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mnemarr.Core.Audit;
using Mnemarr.Core.Declarative;
using Mnemarr.Core.Health;
using Mnemarr.Core.Sources;
using Mnemarr.Core.Storage;

namespace Mnemarr.Core.Test.Declarative;

public sealed class ConfigurationFileServiceFixture : IDisposable
{
    private readonly TestWorkspace _workspace = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 4, 9, 0, 0, TimeSpan.Zero));
    private readonly SqliteSourceStore _store;
    private readonly SqliteAuditLog _audit;
    private readonly ConfigurationState _state = new();
    private readonly string _file;

    public ConfigurationFileServiceFixture()
    {
        _store = new SqliteSourceStore(_workspace.Database);
        _audit = new SqliteAuditLog(_workspace.Database, _clock);
        _file = Path.Combine(_workspace.Root, "mnemarr.yaml");
    }

    public void Dispose() => _workspace.Dispose();

    private ConfigurationFileService Service(string? file, IDatabase? database = null)
    {
        database ??= _workspace.Database;
        var store = new SqliteSourceStore(database);
        var audit = new SqliteAuditLog(database, _clock);
        var catalog = new SourceCatalog(store, audit, _workspace.Paths(), _clock);
        return new ConfigurationFileService(
            new ConfigurationApplier(catalog, store, audit, _clock), _state, Options.Create(new ConfigurationOptions { File = file }), _clock,
            NullLogger<ConfigurationFileService>.Instance);
    }

    private string Yaml(params string[] knowledge) =>
        "knowledge:\n" + string.Concat(knowledge.Select(name => $"  - name: {name}\n    path: {_workspace.Folder(name)}\n"));

    private async Task<HealthCheckResult> Health(string? file) =>
        await new ConfigurationHealthCheck(Options.Create(new ConfigurationOptions { File = file }), _state).CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task should_apply_the_file_and_not_apply_it_again_while_it_does_not_change()
    {
        File.WriteAllText(_file, Yaml("courses"));
        var service = Service(_file);

        await service.ApplyOnceAsync(Token);
        await service.ApplyOnceAsync(Token);

        _store.List().Should().ContainSingle().Which.Should().Match<Source>(s => s.Name == "courses" && s.Managed);
        _audit.Recent(10, "config.applied").Should().ContainSingle();
        _state.Error.Should().BeNull();
        _state.Attempted.Should().BeTrue();
    }

    [Fact]
    public async Task should_apply_the_file_again_when_it_changes_and_remove_what_it_no_longer_lists()
    {
        var service = Service(_file);
        File.WriteAllText(_file, Yaml("courses", "work"));
        await service.ApplyOnceAsync(Token);

        File.WriteAllText(_file, Yaml("courses"));
        await service.ApplyOnceAsync(Token);

        _store.List().Select(s => s.Name).Should().Equal("courses");
    }

    [Fact]
    public async Task should_report_a_file_with_an_empty_entry_instead_of_stopping_the_application()
    {
        File.WriteAllText(_file, "knowledge:\n  -\n");

        var apply = () => Service(_file).ApplyOnceAsync(Token);

        await apply.Should().NotThrowAsync();
        _state.Error.Should().Contain("it is empty");
    }

    [Fact]
    public async Task should_refuse_a_file_larger_than_the_limit_without_reading_it_in()
    {
        File.WriteAllText(_file, new string('#', ConfigurationYaml.MaxBytes + 1));

        await Service(_file).ApplyOnceAsync(Token);

        _state.Error.Should().Contain("larger than");
        _store.List().Should().BeEmpty();
    }

    [Fact]
    public async Task should_say_in_readiness_why_the_file_was_not_applied_without_giving_its_path_and_recover_when_it_is_fixed()
    {
        File.WriteAllText(_file, "memry:\n  path: /x\n");
        var service = Service(_file);

        await service.ApplyOnceAsync(Token);
        var broken = await Health(_file);
        File.WriteAllText(_file, Yaml("courses"));
        await service.ApplyOnceAsync(Token);
        var fixedUp = await Health(_file);

        broken.Status.Should().Be(HealthStatus.Degraded);
        broken.Data["reason"].Should().Be("config-invalid");
        broken.Description.Should().NotContain(_file);
        fixedUp.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task should_try_again_on_the_next_pass_when_a_folder_was_not_there_yet_even_though_the_file_did_not_change()
    {
        var late = Path.Combine(_workspace.SourcesFolder, "late");
        File.WriteAllText(_file, $"knowledge:\n  - name: late\n    path: {late}\n");
        var service = Service(_file);

        await service.ApplyOnceAsync(Token);
        _state.Error.Should().Contain("does not exist");
        Directory.CreateDirectory(late);
        await service.ApplyOnceAsync(Token);

        _state.Error.Should().BeNull();
        _store.List().Should().ContainSingle();
    }

    [Fact]
    public async Task should_say_the_file_cannot_be_read_without_its_path_when_it_is_missing()
    {
        await Service(_file).ApplyOnceAsync(Token);

        _state.Error.Should().Be("The configuration file cannot be read.");
        (await Health(_file)).Description.Should().NotContain(_workspace.Root);
    }

    [Fact]
    public async Task should_stay_up_and_say_so_when_the_database_cannot_be_reached()
    {
        File.WriteAllText(_file, Yaml("courses"));
        var gone = new Database(Options.Create(new Mnemarr.Core.Configuration.MnemarrOptions { DataFolder = Path.Combine(_workspace.Root, "gone") }));

        var apply = () => Service(_file, gone).ApplyOnceAsync(Token);

        await apply.Should().NotThrowAsync();
        _state.Error.Should().Contain("database cannot be reached");
    }

    [Fact]
    public async Task should_do_nothing_and_be_healthy_when_there_is_no_file()
    {
        var service = Service(null);

        await service.StartAsync(Token);
        await service.StopAsync(Token);

        _state.Attempted.Should().BeFalse();
        (await Health(null)).Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task should_be_degraded_as_pending_until_the_file_has_been_tried()
    {
        var result = await Health(_file);

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Data["reason"].Should().Be("config-pending");
    }

    [Theory]
    [InlineData(0, null, "RELOADSECONDS")]
    [InlineData(86_401, null, "RELOADSECONDS")]
    [InlineData(30, "relative/mnemarr.yaml", "absolute")]
    public void should_refuse_settings_that_cannot_work(int seconds, string? file, string named)
    {
        var result = new ConfigurationOptionsValidator().Validate(null, new ConfigurationOptions { ReloadSeconds = seconds, File = file });

        result.FailureMessage.Should().Contain(named, Exactly.Once());
    }

    [Fact]
    public void should_accept_no_file_and_an_absolute_one()
    {
        var validator = new ConfigurationOptionsValidator();

        validator.Validate(null, new ConfigurationOptions()).Succeeded.Should().BeTrue();
        validator.Validate(null, new ConfigurationOptions { File = "/etc/mnemarr/mnemarr.yaml" }).Succeeded.Should().BeTrue();
    }
}
