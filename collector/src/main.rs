use std::io::{self, ErrorKind, Write};
use std::path::PathBuf;
use std::process::ExitCode;
use std::sync::OnceLock;
use std::thread;

use mnemarr_collector::collect::Collector;
use mnemarr_collector::collect::Report;
use mnemarr_collector::config::{
    Config, check_server, config_dir, default_machine, is_machine_name, resolve,
};
use mnemarr_collector::embedded::{self, Embedded};
use mnemarr_collector::enrol;
use mnemarr_collector::service::{self, Installed, Started};
use mnemarr_collector::setup;
use mnemarr_collector::state::State;
use mnemarr_collector::upload::{Heartbeat, HttpUploader, UploadError, Uploader};
use mnemarr_collector::{Command, EnrollOptions, Options, VERSION, parse, usage};

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

/// The file that what is said is also written to, for a collector started in the background.
static LOG_FILE: OnceLock<PathBuf> = OnceLock::new();

/// Says something on standard error: what a service logs. If even that is closed there is nobody left to tell.
fn say(text: &str) {
    let _ = writeln!(io::stderr(), "mnemarr-collector: {text}");
    if let Some(path) = LOG_FILE.get()
        && let Ok(mut file) = std::fs::OpenOptions::new()
            .create(true)
            .append(true)
            .open(path)
    {
        let _ = writeln!(file, "mnemarr-collector: {text}");
    }
}

fn configuration(options: &Options) -> Result<Config, ExitCode> {
    let get = |name: &str| std::env::var(name).ok();
    let read = |path: &str| std::fs::read_to_string(path).ok();
    resolve(options, &get, &read).map_err(|error| {
        let _ = writeln!(io::stderr(), "{error}\n\n{}", usage());
        ExitCode::from(2)
    })
}

/// What a pass tells the server about itself: a short sentence, with no path and no content, when a file could not be read or the server
/// refused a conversation. Nothing when all went well.
fn trouble(report: &Report) -> Option<String> {
    let mut sentences = Vec::new();
    if !report.unreadable.is_empty() {
        sentences.push(format!(
            "{} file(s) could not be read",
            report.unreadable.len()
        ));
    }
    if !report.refused.is_empty() {
        sentences.push(format!(
            "{} conversation(s) were refused by the server",
            report.refused.len()
        ));
    }
    (!sentences.is_empty()).then(|| sentences.join("; "))
}

/// The sign of life that ends a pass. A key that is refused is said, once per pass; the loop goes on, because a person may be about to enrol it again.
fn heartbeat(collector: &Collector<HttpUploader>, report: &Report) {
    let error = trouble(report);
    let beat = Heartbeat {
        version: VERSION,
        pending_lines: report.pending_lines,
        error: error.as_deref(),
    };
    match collector.uploader.heartbeat(&beat) {
        Ok(()) | Err(UploadError::Later(_)) => {}
        Err(UploadError::Refused(status, reason)) => say(&format!(
            "the server refused the sign of life ({status}): {reason}"
        )),
    }
}

/// One pass: what is new is sent, and the position reached is kept. Says whether the pass was done.
fn pass(collector: &Collector<HttpUploader>, config: &Config, state: &mut State) -> bool {
    let report = collector.pass(state);
    heartbeat(collector, &report);
    if let Err(error) = state.save(&config.state) {
        say(&format!(
            "cannot keep the position in {}: {error}",
            config.state.display()
        ));
    }
    // A service that has nothing to say says nothing: a pass that sent something, or that went wrong, is written down.
    if report.lines_sent > 0 || report.stopped.is_some() || !report.refused.is_empty() {
        say(&format!(
            "{} conversation(s), {} line(s) sent",
            report.files, report.lines_sent
        ));
    }
    for refused in &report.refused {
        say(&format!("refused: {refused}"));
    }
    for unreadable in &report.unreadable {
        say(&format!("cannot read: {unreadable}"));
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
    if let Some(path) = &options.log_file {
        let _ = LOG_FILE.set(PathBuf::from(path));
    }
    let config = match configuration(options) {
        Ok(config) => config,
        Err(code) => return code,
    };
    // One collector at a time for a machine: a second one, started by the session while one runs by hand, leaves it be.
    let _instance = if forever {
        let get = |name: &str| std::env::var(name).ok();
        match config_dir(options.config_dir.as_deref(), &get).map(|dir| service::acquire(&dir)) {
            Some(Ok(None)) => {
                say(
                    "another collector is already running for this machine: nothing to do (mnemarr-collector status)",
                );
                return ExitCode::SUCCESS;
            }
            Some(Ok(Some(instance))) => Some(instance),
            Some(Err(error)) => {
                say(&format!(
                    "cannot take the lock of the folder of the collector: {error}"
                ));
                None
            }
            None => None,
        }
    } else {
        None
    };
    let collector = Collector::new(
        config.machine.clone(),
        config.projects.clone(),
        HttpUploader::new(&config.server, &config.key),
    );
    if !collector.masking_is_complete() {
        say(
            "a rule of the masking did not start: secrets are masked by the server alone until this is fixed",
        );
    }
    if config.server.starts_with("http://")
        && !config.server.contains("localhost")
        && !config.server.contains("127.0.0.1")
    {
        say("warning: the address of Mnemarr is plain http://: the key is sent without encryption");
    }
    let mut state = State::load(&config.state);
    if state.adopt(&format!("{}|{}", config.server, config.machine)) {
        say(
            "starting from the beginning for this machine and this server: what was sent before was sent to another",
        );
    }
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

/// Joins this machine: the code a person approved is exchanged for the key of this machine, which is kept where only this user reads it.
fn enroll(options: &EnrollOptions) -> ExitCode {
    let get = |name: &str| std::env::var(name).ok();
    let read = |path: &str| std::fs::read_to_string(path).ok();
    let (Some(server), Some(code)) = (
        options.server.clone().or_else(|| get("MNEMARR_SERVER")),
        options.code.clone(),
    ) else {
        let _ = writeln!(
            io::stderr(),
            "enroll needs --server and --code\n\n{}",
            usage()
        );
        return ExitCode::from(2);
    };
    let Some(machine) = options
        .machine
        .clone()
        .or_else(|| get("MNEMARR_MACHINE"))
        .or_else(|| default_machine(&get, &read))
    else {
        say("the name of this machine is needed: --machine or MNEMARR_MACHINE");
        return ExitCode::from(2);
    };
    if !is_machine_name(&machine) {
        say(&format!(
            "'{machine}' is not a name that Mnemarr accepts for a machine: give another with --machine"
        ));
        return ExitCode::from(2);
    }
    if let Err(error) = check_server(&server, options.allow_insecure_http) {
        say(&error.to_string());
        return ExitCode::from(2);
    }
    let Some(dir) = config_dir(options.config_dir.as_deref(), &get) else {
        say("the place to keep the key cannot be found: give --config-dir");
        return ExitCode::from(2);
    };
    match enrol::exchange(&server, &code, &machine) {
        Ok(enrolled) => match enrol::save(&dir, &server, &enrolled, options.allow_insecure_http) {
            Ok((key, _)) => {
                say(&format!(
                    "enrolled as {} ({}); the key is in {}. Start the collector with: mnemarr-collector run",
                    enrolled.machine,
                    enrolled.kind,
                    key.display()
                ));
                ExitCode::SUCCESS
            }
            Err(error) => {
                say(&format!(
                    "enrolled, but the key could not be kept in {}: {error}. The code is used: ask for another.",
                    dir.display()
                ));
                ExitCode::from(1)
            }
        },
        Err(error) => {
            say(&error.to_string());
            ExitCode::from(1)
        }
    }
}

/// The folder of the collector and the program to run, for the commands that run it in the background. The machine has to be joined first.
fn background(options: &Options) -> Result<(std::path::PathBuf, std::path::PathBuf), ExitCode> {
    configuration(options)?;
    let get = |name: &str| std::env::var(name).ok();
    let (Some(dir), Ok(exe)) = (
        config_dir(options.config_dir.as_deref(), &get),
        std::env::current_exe(),
    ) else {
        say("the folder of the collector cannot be found: give --config-dir");
        return Err(ExitCode::from(2));
    };
    Ok((dir, exe))
}

fn start(options: &Options) -> ExitCode {
    let (dir, exe) = match background(options) {
        Ok(found) => found,
        Err(code) => return code,
    };
    match service::start(&dir, &exe, options.config_dir.as_deref()) {
        Ok(Started::Started) => {
            say(&format!(
                "running in the background (log: {})",
                dir.join(service::LOG_FILE).display()
            ));
            ExitCode::SUCCESS
        }
        Ok(Started::AlreadyRunning) => {
            say("it is already running");
            ExitCode::SUCCESS
        }
        Ok(Started::Failed) => {
            say(&format!(
                "it did not start: see {}",
                dir.join(service::LOG_FILE).display()
            ));
            ExitCode::from(1)
        }
        Err(error) => {
            say(&format!("it could not be started: {error}"));
            ExitCode::from(1)
        }
    }
}

fn install(options: &Options) -> ExitCode {
    let (dir, exe) = match background(options) {
        Ok(found) => found,
        Err(code) => return code,
    };
    match service::install(&dir, &exe, options.config_dir.as_deref()) {
        Ok(Installed::WithSession) => {
            say(
                "installed: it runs in the background now and starts with your session. See: mnemarr-collector status. To stop it: mnemarr-collector stop. To remove it: mnemarr-collector uninstall",
            );
            ExitCode::SUCCESS
        }
        Ok(Installed::RunningOnly(reason)) => {
            say(&format!(
                "it runs in the background now, but it will not start with your session: {reason}"
            ));
            ExitCode::SUCCESS
        }
        Err(error) => {
            say(&format!("it could not be installed: {error}"));
            ExitCode::from(1)
        }
    }
}

fn stop(options: &Options) -> ExitCode {
    let (dir, _) = match background(options) {
        Ok(found) => found,
        Err(code) => return code,
    };
    say(if service::stop(&dir) {
        "stopped. It starts again with your next session if it is installed"
    } else {
        "it was not running"
    });
    ExitCode::SUCCESS
}

fn uninstall(options: &Options) -> ExitCode {
    let get = |name: &str| std::env::var(name).ok();
    let Some(dir) = config_dir(options.config_dir.as_deref(), &get) else {
        say("the folder of the collector cannot be found: give --config-dir");
        return ExitCode::from(2);
    };
    match service::uninstall(&dir) {
        Ok(()) => {
            say(
                "stopped, and it no longer starts with your session. The machine stays joined: to remove it for good, revoke it in Mnemarr and delete the folder of the collector",
            );
            ExitCode::SUCCESS
        }
        Err(error) => {
            say(&format!("it could not be removed: {error}"));
            ExitCode::from(1)
        }
    }
}

fn status(options: &Options) -> ExitCode {
    let get = |name: &str| std::env::var(name).ok();
    let read = |path: &str| std::fs::read_to_string(path).ok();
    let Some(dir) = config_dir(options.config_dir.as_deref(), &get) else {
        say("the folder of the collector cannot be found: give --config-dir");
        return ExitCode::from(2);
    };
    let joined = resolve(options, &get, &read).ok();
    let state = service::status(&dir);
    let _ = writeln!(
        io::stdout(),
        "machine: {}\nserver: {}\nrunning: {}\nstarts with your session: {}\nlog: {}",
        joined
            .as_ref()
            .map_or("not joined (run: mnemarr-collector enroll)", |config| {
                config.machine.as_str()
            }),
        joined.as_ref().map_or("-", |config| config.server.as_str()),
        state
            .running
            .map_or("no".to_string(), |pid| format!("yes (process {pid})")),
        if state.starts_with_session {
            "yes"
        } else {
            "no"
        },
        state.log.display()
    );
    ExitCode::SUCCESS
}

/// What a person is shown when the program they opened sets itself up: said on standard output, and the window waits for them to read it.
fn tell(text: &str) {
    let _ = writeln!(io::stdout(), "{text}");
}

/// A program downloaded from Mnemarr carries its address, the name of the machine and a code: opened with nothing to say, it sets itself up.
fn setup_itself(setting: &Embedded, program: &[u8]) -> ExitCode {
    let get = |name: &str| std::env::var(name).ok();
    let (Some(dir), Some(config)) = (setup::install_dir(&get), config_dir(None, &get)) else {
        tell("The folder of the collector cannot be found on this machine.");
        return ExitCode::from(1);
    };
    tell(&format!(
        "Installing the Mnemarr collector for the machine '{}' (from {})...",
        setting.machine, setting.server
    ));
    if let Err(error) = check_server(&setting.server, setting.insecure) {
        tell(&error.to_string());
        return ExitCode::from(1);
    }
    let result = setup::run(
        setting,
        program,
        &dir,
        &config,
        &|| {
            service::stop(&config);
        },
        &|target| {
            std::process::Command::new(target)
                .arg("install")
                .status()
                .map_err(|error| error.to_string())
                .and_then(|status| {
                    if status.success() {
                        Ok(())
                    } else {
                        Err("the installation said it failed".to_string())
                    }
                })
        },
    );
    let code = match result {
        Ok(_) => {
            tell(
                "\nDone. The collector runs in the background and starts with your session. You can close this window.",
            );
            ExitCode::SUCCESS
        }
        Err(error) => {
            tell(&format!("\n{error}"));
            ExitCode::from(1)
        }
    };
    if std::io::IsTerminal::is_terminal(&io::stdin()) {
        tell("Press Enter to close.");
        let _ = io::stdin().read_line(&mut String::new());
    }
    code
}

/// The setting carried by this very program, and the program without it, when it was opened with nothing to say.
fn carried() -> Option<(Embedded, Vec<u8>)> {
    let (program, setting) = embedded::read(&std::env::current_exe().ok()?).ok()?;
    setting.map(|setting| (setting, program))
}

fn main() -> ExitCode {
    // Arguments that are not valid UTF-8 are kept readable and end up as a usage error, never as a panic.
    let args: Vec<String> = std::env::args_os()
        .skip(1)
        .map(|argument| argument.to_string_lossy().into_owned())
        .collect();
    if args.is_empty()
        && let Some((setting, program)) = carried()
    {
        return setup_itself(&setting, &program);
    }
    match parse(args) {
        Ok(Command::Version) => write_stdout(&format!("mnemarr-collector {VERSION}\n")),
        Ok(Command::Help) => write_stdout(&usage()),
        Ok(Command::Run(options)) => collect(&options, true),
        Ok(Command::Once(options)) => collect(&options, false),
        Ok(Command::Enroll(options)) => enroll(&options),
        Ok(Command::Install(options)) => install(&options),
        Ok(Command::Uninstall(options)) => uninstall(&options),
        Ok(Command::Start(options)) => start(&options),
        Ok(Command::Stop(options)) => stop(&options),
        Ok(Command::Status(options)) => status(&options),
        Err(error) => {
            // If even standard error is closed there is nobody left to tell.
            let _ = writeln!(io::stderr(), "{error}\n\n{}", usage());
            ExitCode::from(2)
        }
    }
}
