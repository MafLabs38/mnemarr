use std::io::{self, ErrorKind, Write};
use std::process::ExitCode;
use std::thread;

use mnemarr_collector::collect::Collector;
use mnemarr_collector::config::{Config, resolve};
use mnemarr_collector::state::State;
use mnemarr_collector::upload::HttpUploader;
use mnemarr_collector::{Command, Options, VERSION, parse, usage};

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

/// Says something on standard error: what a service logs. If even that is closed there is nobody left to tell.
fn say(text: &str) {
    let _ = writeln!(io::stderr(), "mnemarr-collector: {text}");
}

fn configuration(options: &Options) -> Result<Config, ExitCode> {
    let get = |name: &str| std::env::var(name).ok();
    let read = |path: &str| std::fs::read_to_string(path).ok();
    resolve(options, &get, &read).map_err(|error| {
        let _ = writeln!(io::stderr(), "{error}\n\n{}", usage());
        ExitCode::from(2)
    })
}

/// One pass: what is new is sent, and the position reached is kept. Says whether the pass was done.
fn pass(collector: &Collector<HttpUploader>, config: &Config, state: &mut State) -> bool {
    let report = collector.pass(state);
    if let Err(error) = state.save(&config.state) {
        say(&format!(
            "cannot keep the position in {}: {error}",
            config.state.display()
        ));
    }
    say(&format!(
        "{} conversation(s), {} line(s) sent",
        report.files, report.lines_sent
    ));
    for refused in &report.refused {
        say(&format!("refused: {refused}"));
    }
    match &report.stopped {
        Some(reason) => {
            say(&format!("stopped, a later pass will go on: {reason}"));
            false
        }
        None => true,
    }
}

fn collect(options: &Options, forever: bool) -> ExitCode {
    let config = match configuration(options) {
        Ok(config) => config,
        Err(code) => return code,
    };
    let collector = Collector::new(
        config.machine.clone(),
        config.projects.clone(),
        HttpUploader::new(&config.server, &config.key),
    );
    let mut state = State::load(&config.state);
    say(&format!(
        "{VERSION}: {} -> {} as {}",
        config.projects.display(),
        config.server,
        config.machine
    ));
    loop {
        let done = pass(&collector, &config, &mut state);
        if !forever {
            return if done {
                ExitCode::SUCCESS
            } else {
                ExitCode::from(1)
            };
        }
        thread::sleep(config.interval);
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
        Ok(Command::Run(options)) => collect(&options, true),
        Ok(Command::Once(options)) => collect(&options, false),
        Err(error) => {
            // If even standard error is closed there is nobody left to tell.
            let _ = writeln!(io::stderr(), "{error}\n\n{}", usage());
            ExitCode::from(2)
        }
    }
}
