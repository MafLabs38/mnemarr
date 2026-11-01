use std::process::ExitCode;

use mnemarr_collector::{Command, VERSION, parse, usage};

fn main() -> ExitCode {
    // Arguments that are not valid UTF-8 are kept readable and end up as a usage error, never as a panic.
    let args = std::env::args_os()
        .skip(1)
        .map(|argument| argument.to_string_lossy().into_owned());
    match parse(args) {
        Ok(Command::Version) => {
            println!("mnemarr-collector {VERSION}");
            ExitCode::SUCCESS
        }
        Ok(Command::Help) => {
            print!("{}", usage());
            ExitCode::SUCCESS
        }
        Err(error) => {
            eprintln!("{}\n\n{}", error.0, usage());
            ExitCode::from(2)
        }
    }
}
