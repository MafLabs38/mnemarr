//! The Mnemarr collector agent (ADR 0005).
//!
//! This is the walking skeleton: it knows its own version and understands its command line.
//! Reading conversation files, the local queue and the upload come with the collector vertical.

/// Version of this build, from the package metadata.
pub const VERSION: &str = env!("CARGO_PKG_VERSION");

/// What the command line asked for.
#[derive(Debug, PartialEq, Eq)]
pub enum Command {
    Version,
    Help,
}

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
    format!("mnemarr-collector {VERSION}\n\nUSAGE:\n    mnemarr-collector [--version | --help]\n")
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
        other => return Err(unexpected(other)),
    };
    match args.next() {
        None => Ok(command),
        Some(extra) => Err(unexpected(extra.as_ref())),
    }
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
}
