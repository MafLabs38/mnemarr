//! The Mnemarr collector agent (ADR 0005, ADR 0024).
//!
//! It reads the conversation files of Claude Code on this machine, masks secrets, and sends what is new to Mnemarr with an ingest-only key.
//! It remembers how far each file was sent (which is also its queue: a line that did not go is still in the file), so it can be stopped,
//! or the server can be away, without losing anything.

pub mod browser;
pub mod collect;
pub mod config;
pub mod control;
pub mod destinations;
pub mod embedded;
pub mod enrol;
pub mod link_page;
pub mod mask;
pub mod service;
pub mod setup;
pub mod state;
pub mod status;
#[cfg(windows)]
pub mod tray;
pub mod upload;

/// Version of this build, from the package metadata.
pub const VERSION: &str = env!("CARGO_PKG_VERSION");

/// What was given on the command line to `run` and `once`; what is not given comes from the environment, or from the usual places.
#[derive(Debug, Default, PartialEq, Eq, Clone)]
pub struct Options {
    pub server: Option<String>,
    pub key_file: Option<String>,
    pub machine: Option<String>,
    pub projects: Option<String>,
    pub state: Option<String>,
    pub interval_seconds: Option<u64>,
    /// The folder of the collector (the key, the address of Mnemarr), when the usual place is not the right one.
    pub config_dir: Option<String>,
    /// How long `pause` lasts, in minutes: without it, until `resume`.
    pub pause_minutes: Option<u64>,
    /// Do not show an icon in the notification area (Windows shows one by default).
    pub no_tray: bool,
    /// Also write what it says to this file (a collector started in the background has no terminal).
    pub log_file: Option<String>,
    /// Accept an address of Mnemarr that is plain `http://`: the key is then sent without encryption. For a network the person trusts (a sandbox at home).
    pub allow_insecure_http: bool,
}

/// What was given to `enroll`: where Mnemarr is, the code a person approved, and, if it is not the host name, what this machine is called.
#[derive(Debug, Default, PartialEq, Eq, Clone)]
pub struct EnrollOptions {
    pub server: Option<String>,
    pub code: Option<String>,
    pub machine: Option<String>,
    pub config_dir: Option<String>,
    pub allow_insecure_http: bool,
}

/// What was given to `link`: where Mnemarr is, and, if a person already made a code for this machine, that code; without it the machine asks and a person
/// answers in Mnemarr (ADR 0030).
#[derive(Debug, Default, PartialEq, Eq, Clone)]
pub struct LinkOptions {
    pub server: Option<String>,
    pub code: Option<String>,
    /// What this Mnemarr is called in the list (by default the host of its address).
    pub name: Option<String>,
    /// What this machine suggests to be called there (by default its host name); a person may choose another when they answer.
    pub machine: Option<String>,
    pub config_dir: Option<String>,
    pub allow_insecure_http: bool,
    /// Print the address to open instead of opening the browser.
    pub no_browser: bool,
}

/// What was given to `unlink`: the destination to take away.
#[derive(Debug, Default, PartialEq, Eq, Clone)]
pub struct UnlinkOptions {
    pub name: Option<String>,
    pub config_dir: Option<String>,
}

/// What the command line asked for.
#[derive(Debug, PartialEq, Eq)]
pub enum Command {
    /// Link this machine to a Mnemarr, in addition to those it sends to: with a code, or by asking and being approved in Mnemarr.
    Link(LinkOptions),
    /// Stop sending to a Mnemarr.
    Unlink(UnlinkOptions),
    /// List the Mnemarr this machine sends to.
    Destinations(Options),
    Version,
    Help,
    /// Collect for ever: a pass, a wait, a pass.
    Run(Options),
    /// One pass, then stop.
    Once(Options),
    /// Join this machine to Mnemarr with a one-time code, and keep the key.
    Enroll(EnrollOptions),
    /// Have the collector start with the session of the user, and start it now: nothing stays open.
    Install(Options),
    /// Stop it, and take away what starts it with the session. The machine stays joined.
    Uninstall(Options),
    /// Start it in the background now.
    Start(Options),
    /// Stop it now. It starts again with the next session if it is installed.
    Stop(Options),
    /// Say whether it runs, and whether it starts with the session.
    Status(Options),
    /// Be quiet for a while (`--minutes`), or until resumed.
    Pause(Options),
    /// Stop being quiet.
    Resume(Options),
}

/// The flag that accepts a plain `http://` address. It has no value: it is there or it is not.
const ALLOW_INSECURE_HTTP: &str = "--allow-insecure-http";

/// The flag that keeps the icon away. It has no value either.
const NO_TRAY: &str = "--no-tray";

/// The flag that prints the address to open instead of opening the browser. No value.
const NO_BROWSER: &str = "--no-browser";

/// A command line the collector does not understand.
#[derive(Debug, PartialEq, Eq)]
pub struct UsageError(pub String);

impl std::fmt::Display for UsageError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.0)
    }
}

impl std::error::Error for UsageError {}

/// The text printed by `--help`.
pub fn usage() -> String {
    format!(
        "mnemarr-collector {VERSION}\n\n\
USAGE:\n    mnemarr-collector link [--server URL] [--code CODE] [--name NAME] [--machine NAME]\n                                        send to a Mnemarr too: with a code a person made in Mnemarr, or, without\n                                        --code, ask: the address to open is shown, and a person approves there\n    \
mnemarr-collector unlink NAME       stop sending to that Mnemarr (what it received stays there)\n    \
mnemarr-collector destinations      list the Mnemarr this machine sends to\n    \
mnemarr-collector enroll --server URL --code CODE [--machine NAME]\n                                        the same as link with a code\n    \
mnemarr-collector install           start with your session, and start now (in the background)\n    \
mnemarr-collector start | stop      start it in the background, or stop it, now\n    \
mnemarr-collector status            whether it runs, and whether it starts with your session\n    \
mnemarr-collector pause [--minutes N]  be quiet for N minutes (default: until resumed)\n    \
mnemarr-collector resume            stop being quiet\n    \
mnemarr-collector uninstall         stop it and stop it starting with your session (the machine stays joined)\n    \
mnemarr-collector run  [OPTIONS]    collect for ever (a pass every --interval seconds)\n    \
mnemarr-collector once [OPTIONS]    one pass, then stop\n    \
mnemarr-collector [--version | --help]\n\n\
OPTIONS:\n    --server URL        the address of Mnemarr (or MNEMARR_SERVER)\n    \
--key-file PATH     a file that holds the ingest key of this machine (or MNEMARR_COLLECTOR_KEY)\n    \
--machine NAME      the name of this machine (default: its host name; or MNEMARR_MACHINE)\n    \
--projects DIR      the conversations of Claude Code (default: ~/.claude/projects)\n    \
--state FILE        where to remember how far each file was sent\n    \
--interval SECONDS  the wait between two passes (default: 30)\n    \
--config-dir DIR    where the key and the address of Mnemarr are kept (default: the configuration folder of the user)\n    \
--log-file PATH     also write what it says to this file\n    \
--no-tray           no icon in the notification area (Windows)\n    \
--no-browser        (link) print the address to open instead of opening it\n    \
--allow-insecure-http  accept an http:// address (the key then travels without encryption: only for a network you trust)\n"
    )
}

fn unexpected(argument: &str) -> UsageError {
    UsageError(format!("unexpected argument: {argument}"))
}

/// Reads the arguments that follow the program name.
pub fn parse<I, S>(args: I) -> Result<Command, UsageError>
where
    I: IntoIterator<Item = S>,
    S: AsRef<str>,
{
    let mut args = args.into_iter();
    let Some(first) = args.next() else {
        return Ok(Command::Help);
    };
    let command = match first.as_ref() {
        "--version" | "-V" => Command::Version,
        "--help" | "-h" => Command::Help,
        "run" => return Ok(Command::Run(options(args)?)),
        "once" => return Ok(Command::Once(options(args)?)),
        "enroll" => return Ok(Command::Enroll(enroll_options(args)?)),
        "link" => return Ok(Command::Link(link_options(args)?)),
        "unlink" => return Ok(Command::Unlink(unlink_options(args)?)),
        "destinations" => return Ok(Command::Destinations(options(args)?)),
        "install" => return Ok(Command::Install(options(args)?)),
        "uninstall" => return Ok(Command::Uninstall(options(args)?)),
        "start" => return Ok(Command::Start(options(args)?)),
        "stop" => return Ok(Command::Stop(options(args)?)),
        "status" => return Ok(Command::Status(options(args)?)),
        "pause" => return Ok(Command::Pause(options(args)?)),
        "resume" => return Ok(Command::Resume(options(args)?)),
        other => return Err(unexpected(other)),
    };
    match args.next() {
        None => Ok(command),
        Some(extra) => Err(unexpected(extra.as_ref())),
    }
}

/// The options that follow `link`: a flag and its value, each in turn.
fn link_options<I, S>(mut args: I) -> Result<LinkOptions, UsageError>
where
    I: Iterator<Item = S>,
    S: AsRef<str>,
{
    let mut options = LinkOptions::default();
    while let Some(flag) = args.next() {
        let flag = flag.as_ref();
        match flag {
            ALLOW_INSECURE_HTTP => {
                options.allow_insecure_http = true;
                continue;
            }
            NO_BROWSER => {
                options.no_browser = true;
                continue;
            }
            _ => {}
        }
        let Some(value) = args.next() else {
            return Err(UsageError(format!("{flag} needs a value")));
        };
        let value = value.as_ref().to_string();
        match flag {
            "--server" => options.server = Some(value),
            "--code" => options.code = Some(value),
            "--name" => options.name = Some(value),
            "--machine" => options.machine = Some(value),
            "--config-dir" => options.config_dir = Some(value),
            other => return Err(unexpected(other)),
        }
    }
    Ok(options)
}

/// The options that follow `unlink`: the name of the destination, then the flags.
fn unlink_options<I, S>(mut args: I) -> Result<UnlinkOptions, UsageError>
where
    I: Iterator<Item = S>,
    S: AsRef<str>,
{
    let mut options = UnlinkOptions::default();
    while let Some(argument) = args.next() {
        let argument = argument.as_ref();
        if argument == "--config-dir" {
            let Some(value) = args.next() else {
                return Err(UsageError("--config-dir needs a value".to_string()));
            };
            options.config_dir = Some(value.as_ref().to_string());
        } else if argument.starts_with("--") || options.name.is_some() {
            return Err(unexpected(argument));
        } else {
            options.name = Some(argument.to_string());
        }
    }
    Ok(options)
}

/// The options that follow `enroll`: a flag and its value, each in turn.
fn enroll_options<I, S>(mut args: I) -> Result<EnrollOptions, UsageError>
where
    I: Iterator<Item = S>,
    S: AsRef<str>,
{
    let mut options = EnrollOptions::default();
    while let Some(flag) = args.next() {
        let flag = flag.as_ref();
        if flag == ALLOW_INSECURE_HTTP {
            options.allow_insecure_http = true;
            continue;
        }
        let Some(value) = args.next() else {
            return Err(UsageError(format!("{flag} needs a value")));
        };
        let value = value.as_ref().to_string();
        match flag {
            "--server" => options.server = Some(value),
            "--code" => options.code = Some(value),
            "--machine" => options.machine = Some(value),
            "--config-dir" => options.config_dir = Some(value),
            other => return Err(unexpected(other)),
        }
    }
    Ok(options)
}

/// The options that follow `run` or `once`: a flag and its value, each in turn.
fn options<I, S>(mut args: I) -> Result<Options, UsageError>
where
    I: Iterator<Item = S>,
    S: AsRef<str>,
{
    let mut options = Options::default();
    while let Some(flag) = args.next() {
        let flag = flag.as_ref();
        if flag == ALLOW_INSECURE_HTTP {
            options.allow_insecure_http = true;
            continue;
        }
        if flag == NO_TRAY {
            options.no_tray = true;
            continue;
        }
        let Some(value) = args.next() else {
            return Err(UsageError(format!("{flag} needs a value")));
        };
        let value = value.as_ref().to_string();
        match flag {
            "--server" => options.server = Some(value),
            "--key-file" => options.key_file = Some(value),
            "--machine" => options.machine = Some(value),
            "--projects" => options.projects = Some(value),
            "--state" => options.state = Some(value),
            "--log-file" => options.log_file = Some(value),
            "--minutes" => {
                options.pause_minutes = Some(value.parse().map_err(|_| {
                    UsageError(format!("--minutes needs a number of minutes, not {value}"))
                })?);
            }
            "--config-dir" => options.config_dir = Some(value),
            "--interval" => {
                options.interval_seconds = Some(value.parse().map_err(|_| {
                    UsageError(format!("--interval needs a number of seconds, not {value}"))
                })?);
            }
            other => return Err(unexpected(other)),
        }
    }
    Ok(options)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn should_ask_for_the_version_when_given_the_version_flag() {
        assert_eq!(parse(["--version"]), Ok(Command::Version));
        assert_eq!(parse(["-V"]), Ok(Command::Version));
    }

    #[test]
    fn should_show_the_help_when_given_the_help_flag_or_nothing() {
        assert_eq!(parse(["--help"]), Ok(Command::Help));
        assert_eq!(parse(["-h"]), Ok(Command::Help));
        assert_eq!(parse(Vec::<&str>::new()), Ok(Command::Help));
    }

    #[test]
    fn should_refuse_an_unknown_argument() {
        assert_eq!(
            parse(["--nope"]),
            Err(UsageError("unexpected argument: --nope".to_string()))
        );
    }

    #[test]
    fn should_name_the_extra_argument_when_one_follows_a_known_flag() {
        for flag in ["--version", "-V", "--help", "-h"] {
            assert_eq!(
                parse([flag, "extra"]),
                Err(UsageError("unexpected argument: extra".to_string())),
                "after {flag}"
            );
        }
    }

    #[test]
    fn should_read_the_options_of_run_and_once() {
        let expected = Options {
            server: Some("https://mnemarr.example".to_string()),
            key_file: Some("/etc/key".to_string()),
            machine: Some("pc".to_string()),
            projects: None,
            state: None,
            interval_seconds: Some(10),
            config_dir: None,
            log_file: None,
            pause_minutes: None,
            no_tray: false,
            allow_insecure_http: false,
        };
        let arguments = [
            "--server",
            "https://mnemarr.example",
            "--key-file",
            "/etc/key",
            "--machine",
            "pc",
            "--interval",
            "10",
        ];

        assert_eq!(
            parse(["run"].into_iter().chain(arguments)),
            Ok(Command::Run(expected.clone()))
        );
        assert_eq!(
            parse(["once"].into_iter().chain(arguments)),
            Ok(Command::Once(expected))
        );
        assert_eq!(parse(["run"]), Ok(Command::Run(Options::default())));
    }

    #[test]
    fn should_refuse_a_flag_without_a_value_an_unknown_flag_and_an_interval_that_is_not_a_number() {
        assert_eq!(
            parse(["run", "--server"]),
            Err(UsageError("--server needs a value".to_string()))
        );
        assert_eq!(
            parse(["once", "--nope", "x"]),
            Err(UsageError("unexpected argument: --nope".to_string()))
        );
        assert_eq!(
            parse(["run", "--interval", "soon"]),
            Err(UsageError(
                "--interval needs a number of seconds, not soon".to_string()
            ))
        );
    }

    #[test]
    fn should_read_the_options_of_link_with_a_code_and_without() {
        assert_eq!(
            parse([
                "link",
                "--server",
                "https://mnemarr.example",
                "--code",
                "ABCD-EFGH-JKLM",
                "--name",
                "prod",
                "--machine",
                "pc",
                "--config-dir",
                "/c",
                "--allow-insecure-http",
                "--no-browser",
            ]),
            Ok(Command::Link(LinkOptions {
                server: Some("https://mnemarr.example".to_string()),
                code: Some("ABCD-EFGH-JKLM".to_string()),
                name: Some("prod".to_string()),
                machine: Some("pc".to_string()),
                config_dir: Some("/c".to_string()),
                allow_insecure_http: true,
                no_browser: true,
            }))
        );
        assert_eq!(parse(["link"]), Ok(Command::Link(LinkOptions::default())));
        assert_eq!(
            parse(["link", "--code"]),
            Err(UsageError("--code needs a value".to_string()))
        );
        assert_eq!(
            parse(["link", "--nope", "x"]),
            Err(UsageError("unexpected argument: --nope".to_string()))
        );
    }

    #[test]
    fn should_read_the_name_to_unlink_and_the_flags_around_it() {
        assert_eq!(
            parse(["unlink", "dev", "--config-dir", "/c"]),
            Ok(Command::Unlink(UnlinkOptions {
                name: Some("dev".to_string()),
                config_dir: Some("/c".to_string()),
            }))
        );
        assert_eq!(
            parse(["unlink", "--config-dir", "/c", "dev"]),
            Ok(Command::Unlink(UnlinkOptions {
                name: Some("dev".to_string()),
                config_dir: Some("/c".to_string()),
            }))
        );
        assert_eq!(
            parse(["unlink"]),
            Ok(Command::Unlink(UnlinkOptions::default()))
        );
        assert_eq!(
            parse(["unlink", "a", "b"]),
            Err(UsageError("unexpected argument: b".to_string()))
        );
        assert_eq!(
            parse(["unlink", "--config-dir"]),
            Err(UsageError("--config-dir needs a value".to_string()))
        );
    }

    #[test]
    fn should_list_the_destinations() {
        assert_eq!(
            parse(["destinations", "--config-dir", "/c"]),
            Ok(Command::Destinations(Options {
                config_dir: Some("/c".to_string()),
                ..Options::default()
            }))
        );
    }

    #[test]
    fn should_read_the_options_of_enroll() {
        assert_eq!(
            parse([
                "enroll",
                "--server",
                "https://mnemarr.example",
                "--code",
                "ABCD-EFGH-JKLM",
                "--machine",
                "pc",
                "--config-dir",
                "/c"
            ]),
            Ok(Command::Enroll(EnrollOptions {
                server: Some("https://mnemarr.example".to_string()),
                code: Some("ABCD-EFGH-JKLM".to_string()),
                machine: Some("pc".to_string()),
                config_dir: Some("/c".to_string()),
                allow_insecure_http: false,
            }))
        );
        assert_eq!(
            parse(["enroll"]),
            Ok(Command::Enroll(EnrollOptions::default()))
        );
        assert_eq!(
            parse(["enroll", "--code"]),
            Err(UsageError("--code needs a value".to_string()))
        );
        assert_eq!(
            parse(["enroll", "--interval", "5"]),
            Err(UsageError("unexpected argument: --interval".to_string()))
        );
    }

    #[test]
    fn should_take_the_flag_that_allows_plain_http_without_a_value_wherever_it_is() {
        let Ok(Command::Run(options)) = parse(["run", "--allow-insecure-http", "--machine", "pc"])
        else {
            panic!("run was expected");
        };
        assert!(options.allow_insecure_http);
        assert_eq!(options.machine.as_deref(), Some("pc"));
        let Ok(Command::Enroll(options)) =
            parse(["enroll", "--code", "A", "--allow-insecure-http"])
        else {
            panic!("enroll was expected");
        };
        assert!(options.allow_insecure_http);
        assert!(!matches!(parse(["run"]), Ok(Command::Run(o)) if o.allow_insecure_http));
    }

    #[test]
    fn should_read_the_commands_that_run_it_in_the_background() {
        assert_eq!(parse(["install"]), Ok(Command::Install(Options::default())));
        assert_eq!(
            parse(["uninstall"]),
            Ok(Command::Uninstall(Options::default()))
        );
        assert_eq!(parse(["start"]), Ok(Command::Start(Options::default())));
        assert_eq!(parse(["stop"]), Ok(Command::Stop(Options::default())));
        let Ok(Command::Status(options)) = parse(["status", "--config-dir", "/c"]) else {
            panic!("status was expected");
        };
        assert_eq!(options.config_dir.as_deref(), Some("/c"));
    }

    #[test]
    fn should_read_the_commands_that_pause_and_resume_and_the_flag_that_keeps_the_icon_away() {
        let Ok(Command::Pause(options)) = parse(["pause", "--minutes", "60"]) else {
            panic!("pause was expected");
        };
        assert_eq!(options.pause_minutes, Some(60));
        assert!(matches!(parse(["resume"]), Ok(Command::Resume(_))));
        assert_eq!(
            parse(["pause", "--minutes", "soon"]),
            Err(UsageError(
                "--minutes needs a number of minutes, not soon".to_string()
            ))
        );
        let Ok(Command::Run(options)) = parse(["run", "--no-tray"]) else {
            panic!("run was expected");
        };
        assert!(options.no_tray);
    }
}
