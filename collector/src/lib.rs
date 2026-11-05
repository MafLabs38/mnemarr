//! The Mnemarr collector agent (ADR 0005, ADR 0024).
//!
//! It reads the conversation files of Claude Code on this machine, masks secrets, and sends what is new to Mnemarr with an ingest-only key.
//! It remembers how far each file was sent (which is also its queue: a line that did not go is still in the file), so it can be stopped,
//! or the server can be away, without losing anything.

pub mod collect;
pub mod config;
pub mod enrol;
pub mod mask;
pub mod service;
pub mod state;
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

/// What the command line asked for.
#[derive(Debug, PartialEq, Eq)]
pub enum Command {
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
}

/// The flag that accepts a plain `http://` address. It has no value: it is there or it is not.
const ALLOW_INSECURE_HTTP: &str = "--allow-insecure-http";

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
USAGE:\n    mnemarr-collector enroll --server URL --code CODE [--machine NAME]\n                                        join this machine with the code a person approved in Mnemarr\n    \
mnemarr-collector install           start with your session, and start now (in the background)\n    \
mnemarr-collector start | stop      start it in the background, or stop it, now\n    \
mnemarr-collector status            whether it runs, and whether it starts with your session\n    \
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
        "install" => return Ok(Command::Install(options(args)?)),
        "uninstall" => return Ok(Command::Uninstall(options(args)?)),
        "start" => return Ok(Command::Start(options(args)?)),
        "stop" => return Ok(Command::Stop(options(args)?)),
        "status" => return Ok(Command::Status(options(args)?)),
        other => return Err(unexpected(other)),
    };
    match args.next() {
        None => Ok(command),
        Some(extra) => Err(unexpected(extra.as_ref())),
    }
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
}
