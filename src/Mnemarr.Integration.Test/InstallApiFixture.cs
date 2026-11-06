using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mnemarr.Integration.Test;

/// <summary>
/// What a machine fetches to join (ADR 0027 §12): the line a person copies, the installer written for one code and one machine, and the program,
/// served by this Mnemarr with the checksum the installer verifies. Fake programs, so that what is served can be told from what is not.
/// </summary>
public sealed class InstallApiFixture : IClassFixture<DownloadsFactory>, IClassFixture<PublicUrlFactory>, IClassFixture<EnrolledOnlyFactory>
{
    private readonly DownloadsFactory _factory;
    private readonly PublicUrlFactory _behindProxy;
    private readonly EnrolledOnlyFactory _withoutPrograms;

    public InstallApiFixture(DownloadsFactory factory, PublicUrlFactory behindProxy, EnrolledOnlyFactory withoutPrograms)
    {
        _factory = factory;
        _behindProxy = behindProxy;
        _withoutPrograms = withoutPrograms;
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<JsonElement> AddAsync(MnemarrFactoryBase factory, string machine)
    {
        using var admin = await factory.SignedInClientAsync();
        var response = await admin.PostAsJsonAsync("/api/v1/enrolments", new { machine }, Token);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Token);
    }

    private static string Command(JsonElement added, string system) =>
        added.GetProperty("installs").EnumerateArray().Single(item => item.GetProperty("system").GetString() == system).GetProperty("command").GetString()!;

    /// <summary>The path and query of the address inside a line such as <c>irm 'http://host/path?query' | iex</c>.</summary>
    private static string AddressIn(string command) => command.Split('\'')[1];

    [Fact]
    public async Task should_give_one_line_per_system_that_has_a_program_with_the_code_and_the_machine_in_it_and_say_that_the_address_is_not_https()
    {
        var machine = Callers.Unique("pc");

        var added = await AddAsync(_factory, machine);

        var code = added.GetProperty("code").GetString()!;
        Command(added, "windows").Should().StartWith("irm '").And.EndWith("' | iex").And.Contain("/api/v1/ingest/install.ps1?").And.Contain($"code={code}").And.Contain($"machine={machine}");
        Command(added, "unix").Should().StartWith("curl -fsSL '").And.EndWith("' | sh").And.Contain("/api/v1/ingest/install.sh?");
        added.GetProperty("insecure").GetBoolean().Should().BeTrue("the test host speaks plain HTTP");
        added.GetProperty("command").GetString().Should().Contain("--allow-insecure-http");
    }

    [Fact]
    public async Task should_write_the_windows_installer_for_that_code_with_the_checksum_of_the_program_and_nothing_a_person_did_not_expect()
    {
        var machine = Callers.Unique("pc");
        var added = await AddAsync(_factory, machine);
        using var anonymous = _factory.CreateClient();

        var response = await anonymous.GetAsync(AddressIn(Command(added, "windows")), Token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/plain");
        response.Headers.CacheControl!.NoStore.Should().BeTrue("a script that holds a code is not kept");
        var script = await response.Content.ReadAsStringAsync(Token);
        script.Should().Contain(DownloadsFactory.Sha256Of("the windows program")).And.Contain($"--machine '{machine}'").And.Contain(added.GetProperty("code").GetString()!)
            .And.Contain("/api/v1/ingest/download/windows-x86_64").And.Contain("--allow-insecure-http").And.Contain("Get-FileHash").And.Contain("$env:LOCALAPPDATA");
        script.Should().Contain("& $exe install").And.Contain("you can close this window");
        script.Should().NotContain("& $exe run", "an installer installs and hands the window back: it is not the collector");
        script.Should().NotContain("{{", "every placeholder was filled");
        script.Should().NotContain(DownloadsFactory.Sha256Of("the linux program"));
    }

    [Fact]
    public async Task should_write_the_unix_installer_with_a_branch_for_each_system_that_has_a_program()
    {
        var added = await AddAsync(_factory, Callers.Unique("box"));
        using var anonymous = _factory.CreateClient();

        var script = await (await anonymous.GetAsync(AddressIn(Command(added, "unix")), Token)).Content.ReadAsStringAsync(Token);

        script.Should().StartWith("#!/bin/sh").And.Contain($"Linux-x86_64) platform=linux-x86_64; expected={DownloadsFactory.Sha256Of("the linux program")}")
            .And.Contain("This Mnemarr has no collector for");
        script.Should().NotContain("Darwin-arm64", "there is no program for macOS here").And.NotContain("{{");
        script.Should().Contain("\"$exe\" install").And.NotContain("exec \"$exe\" run", "an installer installs and hands the terminal back");
    }

    [Fact]
    public async Task should_offer_a_download_that_already_carries_the_machine_for_windows_and_only_for_it()
    {
        var machine = Callers.Unique("pc");

        var added = await AddAsync(_factory, machine);

        var windows = added.GetProperty("installs").EnumerateArray().Single(item => item.GetProperty("system").GetString() == "windows");
        windows.GetProperty("downloadUrl").GetString().Should().Contain("/api/v1/ingest/download/windows-x86_64?").And.Contain($"machine={machine}").And.Contain($"code={added.GetProperty("code").GetString()}");
        var unix = added.GetProperty("installs").EnumerateArray().Single(item => item.GetProperty("system").GetString() == "unix");
        unix.TryGetProperty("downloadUrl", out var none).Should().BeTrue();
        none.ValueKind.Should().Be(JsonValueKind.Null, "a downloaded file is not made executable by a browser: a terminal line is the way there");
    }

    [Fact]
    public async Task should_serve_the_program_with_the_setting_of_the_machine_after_it_and_name_it_after_the_machine()
    {
        var machine = Callers.Unique("pc");
        var added = await AddAsync(_factory, machine);
        var code = added.GetProperty("code").GetString()!;
        using var anonymous = _factory.CreateClient();

        var response = await anonymous.GetAsync(added.GetProperty("installs").EnumerateArray().First().GetProperty("downloadUrl").GetString()!, Token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentDisposition!.FileName.Should().Be($"mnemarr-collector-{machine}.exe");
        response.Headers.CacheControl!.NoStore.Should().BeTrue("a file that holds a code is not kept");
        var bytes = await response.Content.ReadAsByteArrayAsync(Token);
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        text.Should().StartWith("the windows program").And.EndWith("MNMRCFG1");
        text.Should().Contain($"\"machine\":\"{machine}\"").And.Contain($"\"code\":\"{code}\"").And.Contain("\"insecure\":true").And.Contain("\"server\":\"http://");
        BitConverter.ToUInt32(bytes, bytes.Length - 12).Should().BeLessThan(300u);
    }

    [Fact]
    public async Task should_refuse_to_write_a_download_for_a_code_or_a_machine_that_is_not_one_or_a_system_that_has_no_program()
    {
        using var anonymous = _factory.CreateClient();

        (await anonymous.GetAsync("/api/v1/ingest/download/windows-x86_64?code=ABCD-EFGH-JKLM", Token)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await anonymous.GetAsync("/api/v1/ingest/download/windows-x86_64?machine=pc", Token)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await anonymous.GetAsync("/api/v1/ingest/download/windows-x86_64?code=ABCD-EFGH-JKLM&machine=a%27%3Bcalc", Token)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await anonymous.GetAsync("/api/v1/ingest/download/macos-aarch64?code=ABCD-EFGH-JKLM&machine=pc", Token)).StatusCode.Should().Be(HttpStatusCode.BadRequest, "a caller without an approved code is not told which systems there are programs for");
    }

    [Fact]
    public async Task should_write_a_download_only_for_a_code_that_a_person_approved_for_that_machine()
    {
        var machine = Callers.Unique("pc");
        var added = await AddAsync(_factory, machine);
        var code = added.GetProperty("code").GetString();
        using var anonymous = _factory.CreateClient();

        (await anonymous.GetAsync($"/api/v1/ingest/download/windows-x86_64?code=BCDF-GHJK-MNPQ&machine={machine}", Token)).StatusCode.Should().Be(HttpStatusCode.BadRequest, "a code that no one was given");
        (await anonymous.GetAsync($"/api/v1/ingest/download/windows-x86_64?code={code}&machine=another-pc", Token)).StatusCode.Should().Be(HttpStatusCode.BadRequest, "a code for another machine");
        var program = await anonymous.GetAsync($"/api/v1/ingest/download/windows-x86_64?code={code}&machine={machine}", Token);
        program.StatusCode.Should().Be(HttpStatusCode.OK);
        var bytes = await program.Content.ReadAsByteArrayAsync(Token);
        program.Content.Headers.ContentLength.Should().Be(bytes.Length, "the length that is announced is the one that is sent");
        System.Text.Encoding.UTF8.GetString(bytes).Should().StartWith("the windows program").And.EndWith("MNMRCFG1");
        var settingLength = (int)BitConverter.ToUInt32(bytes, bytes.Length - 12);
        bytes.Length.Should().Be("the windows program".Length + settingLength + 12, "the program, its setting, the length of the setting and the mark: nothing else");
    }

    [Fact]
    public async Task should_refuse_a_download_for_a_code_that_waits_for_a_person_or_has_expired()
    {
        var callers = await Callers.ForAsync(_factory);
        var waiting = Callers.Unique("pc");
        var asked = await callers["Assistant"].PostAsJsonAsync("/api/v1/enrolments/requests", new { machine = waiting }, Token);
        var waitingCode = (await asked.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("code").GetString();
        var late = Callers.Unique("pc");
        var lateCode = (await AddAsync(_factory, late)).GetProperty("code").GetString();
        using var anonymous = _factory.CreateClient();

        (await anonymous.GetAsync($"/api/v1/ingest/download/windows-x86_64?code={waitingCode}&machine={waiting}", Token)).StatusCode.Should().Be(HttpStatusCode.BadRequest, "no person has approved it");
        _factory.Clock.Advance(TimeSpan.FromMinutes(16));
        (await anonymous.GetAsync($"/api/v1/ingest/download/windows-x86_64?code={lateCode}&machine={late}", Token)).StatusCode.Should().Be(HttpStatusCode.BadRequest, "the code lives fifteen minutes");
    }

    [Fact]
    public async Task should_serve_the_program_of_a_system_as_a_file_and_nothing_else_of_the_folder()
    {
        using var anonymous = _factory.CreateClient();

        var program = await anonymous.GetAsync("/api/v1/ingest/download/windows-x86_64", Token);

        program.StatusCode.Should().Be(HttpStatusCode.OK);
        (await program.Content.ReadAsStringAsync(Token)).Should().Be("the windows program");
        program.Content.Headers.ContentDisposition!.FileName.Should().Be("mnemarr-collector-windows-x86_64.exe");
        foreach (var other in new[] { "macos-aarch64", "not-served.txt", "..%2f..%2fetc%2fpasswd", "windows-x86_64.exe", "WINDOWS-X86_64" })
        {
            (await anonymous.GetAsync($"/api/v1/ingest/download/{other}", Token)).StatusCode.Should().Be(HttpStatusCode.NotFound, other);
        }
    }

    [Theory]
    [InlineData("a'; Remove-Item -Recurse C:\\ #")]
    [InlineData("pc$(reboot)")]
    [InlineData("pc name")]
    [InlineData("")]
    public async Task should_refuse_to_write_an_installer_for_a_machine_name_that_could_end_a_string_or_add_a_command(string machine)
    {
        using var anonymous = _factory.CreateClient();

        foreach (var script in new[] { "install.ps1", "install.sh" })
        {
            var response = await anonymous.GetAsync($"/api/v1/ingest/{script}?code=ABCD-EFGH-JKLM&machine={Uri.EscapeDataString(machine)}", Token);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, script);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("ABCD-EFGH")]
    [InlineData("ABCD-EFGH-JKLM'; calc")]
    [InlineData("0000-0000-0000")]
    public async Task should_refuse_to_write_an_installer_for_a_code_that_is_not_one(string code)
    {
        using var anonymous = _factory.CreateClient();

        var response = await anonymous.GetAsync($"/api/v1/ingest/install.ps1?code={Uri.EscapeDataString(code)}&machine=pc", Token);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task should_use_the_address_it_was_given_behind_a_proxy_and_not_ask_for_plain_http_to_be_accepted()
    {
        var added = await AddAsync(_behindProxy, Callers.Unique("pc"));
        using var anonymous = _behindProxy.CreateClient();

        var command = Command(added, "windows");
        var script = await (await anonymous.GetAsync(command["irm '".Length..command.IndexOf('\'', 5)].Replace("https://mnemarr.homelab.example", string.Empty, StringComparison.Ordinal), Token)).Content.ReadAsStringAsync(Token);

        command.Should().StartWith("irm 'https://mnemarr.homelab.example/api/v1/ingest/install.ps1?");
        added.GetProperty("insecure").GetBoolean().Should().BeFalse();
        script.Should().Contain("$server = 'https://mnemarr.homelab.example'").And.NotContain("--allow-insecure-http");
    }

    [Fact]
    public async Task should_offer_no_line_and_write_no_installer_when_it_has_no_program_to_give()
    {
        var added = await AddAsync(_withoutPrograms, Callers.Unique("pc"));
        using var anonymous = _withoutPrograms.CreateClient();

        added.GetProperty("installs").GetArrayLength().Should().Be(0);
        (await anonymous.GetAsync("/api/v1/ingest/install.ps1?code=ABCD-EFGH-JKLM&machine=pc", Token)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await anonymous.GetAsync("/api/v1/ingest/download/windows-x86_64", Token)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task should_tell_an_assistant_that_asks_over_mcp_the_line_to_run_and_that_the_address_is_not_https()
    {
        var callers = await Callers.ForAsync(_factory);

        var response = await callers["Assistant"].PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = "request_collector_enrolment", arguments = new { machine = Callers.Unique("pc") } } }, Token);

        var result = JsonNode.Parse(await response.Content.ReadAsStringAsync(Token))!["result"]!;
        result["isError"]!.GetValue<bool>().Should().BeFalse();
        var reply = JsonNode.Parse(result["content"]![0]!["text"]!.GetValue<string>())!;
        reply["install"]!.AsArray().Select(item => item!["system"]!.GetValue<string>()).Should().BeEquivalentTo(["windows", "unix"]);
        reply["install"]![0]!["command"]!.GetValue<string>().Should().Contain("install.ps1?code=");
        reply["insecure"]!.GetValue<bool>().Should().BeTrue();
        reply["joinOnly"]!.GetValue<string>().Should().Contain("mnemarr-collector enroll");
    }
}
