use std::io::{self, ErrorKind, Write};
use std::process::ExitCode;

use mnemarr_collector::{Command, VERSION, parse, usage};

/// Writes to standard output. A closed pipe (`--help | head`) is how a reader says it has enough, not a failure.
fn write_stdout(text: &str) -> ExitCode {
    match io::stdout().write_all(text.as_bytes()) {
        Ok(()) => ExitCode::SUCCESS,
        Err(error) if error.kind() == ErrorKind::BrokenPipe => ExitCode::SUCCESS,
        Err(error) => {
            // If even standard error is closed there is nobody left to tell.
            let _ = writeln!(
                io::stderr(),
                "mnemarr-collector: cannot write to standard output: {error}"
            );
            ExitCode::from(1)
        }
    }
}

fn main() -> ExitCode {
    // Arguments that are not valid UTF-8 are kept readable and end up as a usage error, never as a panic.
    let args = std::env::args_os()
        .skip(1)
        .map(|argument| argument.to_string_lossy().into_owned());
    match parse(args) {
        Ok(Command::Version) => write_stdout(&format!("mnemarr-collector {VERSION}\n")),
        Ok(Command::Help) => write_stdout(&usage()),
        Err(error) => {
            // If even standard error is closed there is nobody left to tell.
            let _ = writeln!(io::stderr(), "{error}\n\n{}", usage());
            ExitCode::from(2)
        }
    }
}
