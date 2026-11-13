using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mnemarr.Core.Audit;
using Mnemarr.Core.Identity;

namespace Mnemarr.Core.Test.Identity;

/// <summary>
/// The identity, whole (ADR 0044): what an administrator set wins, then what the deployment said, then the product's own; a change that is not valid changes nothing; a logo is checked
/// before it is kept; every change is audited.
/// </summary>
public sealed class IdentityServiceFixture : IDisposable
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
    private readonly TestWorkspace _workspace = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 13, 9, 0, 0, TimeSpan.Zero));
    private readonly SqliteAuditLog _audit;
    private readonly SqliteIdentityStore _store;

    public IdentityServiceFixture()
    {
        _audit = new SqliteAuditLog(_workspace.Database, _clock);
        _store = new SqliteIdentityStore(_workspace.Database);
    }

    public void Dispose() => _workspace.Dispose();

    private IdentityService Service(BrandingOptions? options = null) => new(_store, _audit, Options.Create(options ?? new BrandingOptions()), _clock);

    private string Write(string name, byte[] content)
    {
        var path = Path.Combine(_workspace.Root, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    [Fact]
    public void should_be_the_product_when_nothing_is_said()
    {
        var identity = Service().Get();

        identity.Should().Be(new ResolvedIdentity("Mnemarr", new IdentityColors("accent", "accent"), null, null));
    }

    [Fact]
    public void should_take_what_the_deployment_says_until_an_administrator_says_something_else()
    {
        var options = new BrandingOptions { Name = "Example Lab", Colors = new BrandingColorOptions { Name = "#3ee0c5", Version = "inkMuted" } };
        var service = Service(options);

        service.Get().Should().Match<ResolvedIdentity>(identity => identity.Name == "Example Lab" && identity.Colors.Name == "#3ee0c5" && identity.Colors.Version == "inkMuted");
        service.Set(new IdentityChange(Name: "Sandbox"), "admin").Name.Should().Be("Sandbox", "what is set in the interface wins");
        service.Set(new IdentityChange(Name: string.Empty), "admin").Name.Should().Be("Example Lab", "an empty name goes back to the deployment's");
    }

    [Fact]
    public void should_change_only_what_is_given_and_forget_what_is_sent_empty()
    {
        var service = Service();
        service.Set(new IdentityChange("Lab", "#fff", "ink"), "admin");

        var onlyTheName = service.Set(new IdentityChange(Name: "Lab 2"), "admin");
        var cleared = service.Set(new IdentityChange(NameColor: " ", VersionColor: string.Empty), "admin");

        onlyTheName.Should().Match<ResolvedIdentity>(identity => identity.Name == "Lab 2" && identity.Colors.Name == "#fff" && identity.Colors.Version == "ink");
        cleared.Should().Match<ResolvedIdentity>(identity => identity.Name == "Lab 2" && identity.Colors.Name == "accent" && identity.Colors.Version == "accent");
    }

    [Theory]
    [InlineData("two\nlines", null, null)]
    [InlineData(null, "red", null)]
    [InlineData(null, null, "url(x)")]
    public void should_refuse_a_value_that_is_not_valid_and_change_nothing(string? name, string? nameColor, string? versionColor)
    {
        var service = Service();
        service.Set(new IdentityChange("Lab", "#fff", "ink"), "admin");

        var act = () => service.Set(new IdentityChange(name, nameColor, versionColor), "admin");

        act.Should().Throw<RejectedException>().Which.Problem.Should().Be(Problem.Invalid);
        service.Get().Should().Match<ResolvedIdentity>(identity => identity.Name == "Lab" && identity.Colors.Name == "#fff" && identity.Colors.Version == "ink");
    }

    [Fact]
    public void should_keep_a_logo_that_was_checked_serve_it_with_its_hash_and_use_the_product_s_own_again_when_it_is_removed()
    {
        var service = Service();

        var set = service.SetLogo("lab.PNG", Png, "admin");
        var content = set.Logo!.Load();
        var removed = service.RemoveLogo("admin");

        set.Logo.Should().Match<IdentityLogoFile>(logo => logo.ContentType == "image/png" && logo.Hash.Length == 64 && logo.Modified == _clock.GetUtcNow());
        content.Should().Equal(Png);
        removed.Logo.Should().BeNull();
    }

    [Fact]
    public void should_refuse_a_logo_that_is_not_what_its_extension_says_and_keep_the_one_it_had()
    {
        var service = Service();
        service.SetLogo("lab.png", Png, "admin");

        var act = () => service.SetLogo("lab.png", "<html></html>"u8.ToArray(), "admin");

        act.Should().Throw<RejectedException>().Which.Problem.Should().Be(Problem.Invalid);
        service.Get().Logo!.Load().Should().Equal(Png);
    }

    [Fact]
    public void should_change_the_hash_of_the_address_with_the_file_so_that_a_new_logo_is_not_served_from_a_cache()
    {
        var service = Service();
        var first = service.SetLogo("a.png", Png, "admin").Logo!.Hash;

        var second = service.SetLogo("a.png", [.. Png, 9], "admin").Logo!.Hash;

        second.Should().NotBe(first);
    }

    [Fact]
    public void should_use_the_logo_file_of_the_deployment_until_one_is_uploaded_and_after_it_is_removed()
    {
        var service = Service(new BrandingOptions { LogoFile = Write("lab.png", Png) });

        service.Get().Logo!.Load().Should().Equal(Png);
        service.SetLogo("mine.png", [.. Png, 7], "admin").Logo!.Load().Should().Equal([.. Png, 7]);
        service.RemoveLogo("admin").Logo!.Load().Should().Equal(Png);
    }

    [Fact]
    public void should_read_the_logo_file_of_the_deployment_again_when_it_changes()
    {
        var path = Write("lab.png", Png);
        var service = Service(new BrandingOptions { LogoFile = path });
        var before = service.Get().Logo!.Hash;

        File.WriteAllBytes(path, [.. Png, 4, 4, 4]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

        service.Get().Logo!.Hash.Should().NotBe(before);
    }

    [Fact]
    public void should_not_read_the_logo_file_of_the_deployment_again_while_it_has_not_changed()
    {
        var path = Write("lab.png", Png);
        var service = Service(new BrandingOptions { LogoFile = path });
        var first = service.Get().Logo!;

        File.Delete(path);
        File.WriteAllBytes(path, Png);
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path));

        service.Get().Logo!.Hash.Should().Be(first.Hash);
    }

    [Fact]
    public void should_say_why_the_logo_file_of_the_deployment_is_not_used_without_giving_its_path()
    {
        var missing = Service(new BrandingOptions { LogoFile = Path.Combine(_workspace.Root, "nowhere.png") }).Get();
        var wrong = Service(new BrandingOptions { LogoFile = Write("fake.png", "not an image"u8.ToArray()) }).Get();
        var large = Service(new BrandingOptions { LogoFile = Write("big.png", [.. Png, .. new byte[IdentityRules.MaxLogoBytes]]) }).Get();

        missing.Should().Match<ResolvedIdentity>(identity => identity.Logo == null && identity.LogoProblem!.Contains("not there", StringComparison.Ordinal));
        wrong.Should().Match<ResolvedIdentity>(identity => identity.Logo == null && identity.LogoProblem!.Contains("not a valid png", StringComparison.Ordinal));
        large.Should().Match<ResolvedIdentity>(identity => identity.Logo == null && identity.LogoProblem!.Contains("larger than", StringComparison.Ordinal));
        new[] { missing, wrong, large }.Should().OnlyContain(identity => !identity.LogoProblem!.Contains(_workspace.Root, StringComparison.Ordinal));
    }

    [Fact]
    public void should_audit_every_change_under_the_name_of_who_made_it_and_say_nothing_of_the_values()
    {
        var service = Service();

        service.Set(new IdentityChange("Secret lab name"), "alice");
        service.SetLogo("a.png", Png, "alice");
        service.RemoveLogo("bob");

        var events = _audit.Recent(10);
        events.Select(entry => (entry.Actor, entry.Action)).Should().Equal(("bob", "identity.logo.removed"), ("alice", "identity.logo.updated"), ("alice", "identity.updated"));
        events.Should().OnlyContain(entry => !entry.Detail.Contains("Secret", StringComparison.Ordinal) && entry.Target == "identity");
    }

    [Fact]
    public void should_not_read_the_content_of_the_logo_to_say_what_the_identity_is()
    {
        var service = Service();
        service.SetLogo("a.png", Png, "admin");

        var identity = service.Get();

        identity.Logo!.Hash.Should().HaveLength(64);
        identity.Logo.Load().Should().Equal(Png, "the bytes are read when the image is sent, by whoever sends it");
    }

    [Fact]
    public void should_not_undo_a_change_of_the_texts_with_a_change_of_the_logo_and_the_other_way_round()
    {
        var service = Service();
        service.SetLogo("a.png", Png, "admin");
        service.Set(new IdentityChange("Lab", "#fff", null), "admin");

        service.RemoveLogo("admin");
        var afterRemoval = service.Get();
        service.SetLogo("b.png", Png, "admin");

        afterRemoval.Should().Match<ResolvedIdentity>(identity => identity.Name == "Lab" && identity.Colors.Name == "#fff" && identity.Logo == null);
        service.Get().Should().Match<ResolvedIdentity>(identity => identity.Name == "Lab" && identity.Logo != null);
    }

    [Fact]
    public void should_keep_what_is_set_between_two_services()
    {
        Service().Set(new IdentityChange("Lab", "#fff", null), "admin");
        Service().SetLogo("a.png", Png, "admin");

        Service().Get().Should().Match<ResolvedIdentity>(identity => identity.Name == "Lab" && identity.Colors.Name == "#fff" && identity.Logo != null);
    }

    [Theory]
    [InlineData("two\nlines", null, null, "one line")]
    [InlineData(null, "red", null, "name color")]
    [InlineData(null, null, "red", "version color")]
    public void should_refuse_a_deployment_setting_that_is_not_valid_at_start(string? name, string? nameColor, string? versionColor, string expected)
    {
        var options = new BrandingOptions { Name = name, Colors = new BrandingColorOptions { Name = nameColor, Version = versionColor } };

        var result = new BrandingOptionsValidator().Validate(null, options);

        result.FailureMessage.Should().Contain("MNEMARR__IDENTITY").And.Contain(expected);
    }

    [Fact]
    public void should_accept_the_settings_of_a_deployment_that_says_nothing_or_says_it_right()
    {
        new BrandingOptionsValidator().Validate(null, new BrandingOptions()).Succeeded.Should().BeTrue();
        new BrandingOptionsValidator().Validate(null, new BrandingOptions { Name = "Example Lab", Colors = new BrandingColorOptions { Name = "accent", Version = "#fff" }, LogoFile = "/branding/a.png" }).Succeeded.Should().BeTrue();
    }
}
