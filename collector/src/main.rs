use std::collections::HashMap;
use std::io::{self, ErrorKind, Write};
use std::path::PathBuf;
use std::process::ExitCode;
use std::sync::OnceLock;
use std::thread;

use mnemarr_collector::browser;
use mnemarr_collector::collect::Collector;
use mnemarr_collector::collect::Report;
use mnemarr_collector::config::{
    Config, check_server, config_dir, default_machine, is_machine_name, resolve_all,
};
use mnemarr_collector::control;
use mnemarr_collector::destinations;
use mnemarr_collector::embedded::{self, Embedded};
use mnemarr_collector::enrol::{self, LinkOutcome};
use mnemarr_collector::service::{self, Installed, Started};
use mnemarr_collector::setup;
use mnemarr_collector::state::State;
use mnemarr_collector::status::{self, Condition};
use mnemarr_collector::update::{self, UpdateState, Updater};
use mnemarr_collector::upload::{Heartbeat, HttpUploader, UploadError, Uploader};
use mnemarr_collector::{
    Command, EnrollOptions, LinkOptions, Options, UnlinkOptions, UpdateOptions, VERSION, parse,
    usage,
};

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

/// Whether this program was installed with the installer, and where an update is at: found out once.
fn updater() -> &'static Updater {
    static UPDATER: OnceLock<Updater> = OnceLock::new();
    UPDATER.get_or_init(Updater::detect)
}

/// The version this program says it is: the one the installer knows when it was installed with it, otherwise the one it was built with.
fn version_text() -> String {
    updater().version().unwrap_or_else(|| VERSION.to_string())
}

/// How often an installed collector asks the Mnemarr it sends to for a newer version, and how long after it starts it first does (a minute and a half
/// lets the first pass go through, and lets a computer that has just woken find its network).
const CHECK_EVERY: std::time::Duration = std::time::Duration::from_secs(4 * 3600);
const FIRST_CHECK: std::time::Duration = std::time::Duration::from_secs(90);

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

/// What a pass runs with, for each Mnemarr this machine sends to (ADR 0030), or why there is none.
fn resolve_configs(options: &Options) -> Result<Vec<Config>, mnemarr_collector::UsageError> {
    let get = |name: &str| std::env::var(name).ok();
    let read = |path: &str| std::fs::read_to_string(path).ok();
    resolve_all(options, &get, &read)
}

/// The same, for a command that was just typed: what is wrong is said with the usage, and the command ends.
fn configurations(options: &Options) -> Result<Vec<Config>, ExitCode> {
    resolve_configs(options).map_err(|error| {
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

/// The sign of life that ends a pass (and a pause: it still says it is there, and why it sends nothing). A key that is refused is said, once per
/// pass; the loop goes on, because a person may be about to enrol it again.
fn heartbeat(
    collector: &Collector<HttpUploader>,
    report: &Report,
    paused: bool,
    interval_seconds: u64,
) -> Result<(), UploadError> {
    let error = trouble(report);
    let version = version_text();
    let beat = Heartbeat {
        version: &version,
        can_update: updater().installed(),
        update_note: updater().note(),
        pending_lines: report.pending_lines,
        error: error.as_deref(),
        paused,
        interval_seconds,
    };
    let result = collector.uploader.heartbeat(&beat);
    if let Err(UploadError::Refused(status, reason)) = &result {
        say(&format!(
            "the server refused the sign of life ({status}): {reason}"
        ));
    }
    result
}

/// How the collector is after a pass, for the icon.
fn condition_after(report: &Report, beat: &Result<(), UploadError>, paused: bool) -> Condition {
    if paused {
        return Condition::Paused;
    }
    if let Err(UploadError::Refused(401 | 403, _)) = beat {
        return Condition::Refused;
    }
    if let Some(reason) = &report.stopped {
        return Condition::Offline(reason.chars().take(60).collect());
    }
    if let Err(UploadError::Later(reason)) = beat {
        return Condition::Offline(reason.chars().take(60).collect());
    }
    trouble(report).map_or(Condition::Online, Condition::Problem)
}

/// One pass for one Mnemarr: what is new is sent, and the position reached is kept. Says whether the pass was done, and how that Mnemarr is. When the person
/// paused the collector, nothing is read or sent, and only the sign of life goes.
fn pass(
    collector: &Collector<HttpUploader>,
    config: &Config,
    state: &mut State,
    paused: bool,
) -> (bool, Condition) {
    let report = if paused {
        Report::default()
    } else {
        collector.pass(state)
    };
    let beat = heartbeat(collector, &report, paused, config.interval.as_secs());
    let condition = condition_after(&report, &beat, paused);
    if paused {
        return (true, condition);
    }
    if let Err(error) = state.save(&config.state) {
        say(&format!(
            "{}: cannot keep the position in {}: {error}",
            config.name,
            config.state.display()
        ));
    }
    // A service that has nothing to say says nothing: a pass that sent something, or that went wrong, is written down.
    if report.lines_sent > 0 || report.stopped.is_some() || !report.refused.is_empty() {
        say(&format!(
            "{}: {} conversation(s), {} line(s) sent",
            config.name, report.files, report.lines_sent
        ));
    }
    for refused in &report.refused {
        say(&format!("{}: refused: {refused}", config.name));
    }
    for unreadable in &report.unreadable {
        say(&format!("{}: cannot read: {unreadable}", config.name));
    }
    match &report.stopped {
        Some(reason) => {
            say(&format!(
                "{}: stopped, a later pass will go on: {reason}",
                config.name
            ));
            (false, condition)
        }
        None => (true, condition),
    }
}

/// One pass over every Mnemarr: each has its own positions and its own sign of life, one that cannot be reached does not hold the others back, and what is
/// read from the files is read again for each (a local read; the masking is the same).
fn pass_all(
    configs: &[Config],
    states: &mut HashMap<String, State>,
    paused: bool,
    condition: &status::Shared,
) -> bool {
    let mut all_done = true;
    let mut conditions = Vec::new();
    for config in configs {
        let state = states
            .entry(config.name.clone())
            .or_insert_with(|| State::load(&config.state));
        if state.adopt(&format!("{}|{}", config.server, config.machine)) {
            say(&format!(
                "{}: starting from the beginning for this machine and this server: what was sent before was sent to another",
                config.name
            ));
        }
        let collector = Collector::new(
            config.machine.clone(),
            config.projects.clone(),
            HttpUploader::new(&config.server, &config.key),
        );
        let (done, how) = pass(&collector, config, state, paused);
        all_done &= done;
        conditions.push((config.name.clone(), how));
    }
    status::set_all(condition, conditions, paused);
    all_done
}

/// Collects: a pass, a wait, a pass; for ever when asked to, else once. A pause the person asked for (from the icon or `pause`) is looked at before each pass,
/// and so is the list of Mnemarr: one that is linked while it runs is taken from the next pass on.
fn collect_loop(
    options: &Options,
    initial: Vec<Config>,
    dir: Option<PathBuf>,
    forever: bool,
    condition: status::Shared,
) -> ExitCode {
    let mut states: HashMap<String, State> = HashMap::new();
    let mut configs = initial;
    let mut was_paused = false;
    let mut told_none = false;
    let mut last_problem = String::new();
    let mut told_problems: std::collections::HashSet<String> = std::collections::HashSet::new();
    let mut clock = UpdateClock::new();
    loop {
        let paused = dir
            .as_deref()
            .is_some_and(|dir| control::current(dir, std::time::SystemTime::now()).is_some());
        if paused != was_paused {
            say(if paused { "paused" } else { "resumed" });
            was_paused = paused;
        }
        let done = if configs.is_empty() {
            if !told_none {
                say(
                    "this machine is not linked to any Mnemarr: link one from the icon, or with: mnemarr-collector link",
                );
                told_none = true;
            }
            status::set(
                &condition,
                Condition::Offline("not linked to a Mnemarr".to_string()),
            );
            false
        } else {
            told_none = false;
            pass_all(&configs, &mut states, paused, &condition)
        };
        if !forever {
            return if done {
                ExitCode::SUCCESS
            } else {
                ExitCode::from(1)
            };
        }
        if let Some(dir) = &dir {
            keep_up_to_date(dir, &mut clock);
        }
        thread::sleep(
            configs
                .first()
                .map_or(std::time::Duration::from_secs(30), |config| config.interval),
        );
        // The list is read again: a Mnemarr linked from the icon, or by `link`, is sent to from this pass on. What is wrong with it is said once.
        match resolve_configs(options) {
            Ok(fresh) => {
                last_problem.clear();
                configs = fresh;
                // A destination that is left out while the others go on is said too, once, with the reason.
                let get = |name: &str| std::env::var(name).ok();
                for (name, reason) in mnemarr_collector::config::problems(options, &get) {
                    if told_problems.insert(format!("{name}: {reason}")) {
                        say(&format!("{name} is left out of the passes: {reason}"));
                    }
                }
            }
            Err(error) => {
                if last_problem != error.0 {
                    say(&error.0);
                    last_problem = error.0;
                }
                configs.clear();
            }
        }
    }
}

/// When an installed collector next asks for an update, and when it may next try to apply one that is ready (a failure to apply is not tried again at every pass).
struct UpdateClock {
    next_check: std::time::Instant,
    apply_after: std::time::Instant,
}

impl UpdateClock {
    fn new() -> Self {
        let now = std::time::Instant::now();
        Self {
            next_check: now + FIRST_CHECK,
            apply_after: now,
        }
    }
}

/// Between two passes, for a collector that was installed with the installer: an update that is ready is applied (unless the person chose to apply them by
/// hand), and, now and then, each Mnemarr is asked for a newer version, apart from the passes. Positions are kept after each pass, so nothing is lost when the
/// collector restarts to apply it. An update that waits is neither asked for again nor downloaded again; one that could not be applied is tried again later,
/// not at every pass.
fn keep_up_to_date(dir: &std::path::Path, clock: &mut UpdateClock) {
    let updater = updater();
    if !updater.installed() {
        return;
    }
    let now = std::time::Instant::now();
    if updater.ready() {
        if update::load_settings(dir).auto && now >= clock.apply_after {
            say("applying the update: the collector restarts");
            if let Err(error) = updater.apply() {
                say(&format!("the update could not be applied: {error}"));
                clock.apply_after = now + CHECK_EVERY;
            }
        }
        return;
    }
    if now >= clock.next_check {
        clock.next_check = now + CHECK_EVERY;
        let checker = updater.clone();
        let listed = destinations::load(dir);
        thread::spawn(move || match checker.check(&listed) {
            UpdateState::Ready(version) => say(&format!("version {version} is downloaded")),
            UpdateState::Failed(reason) => say(&format!("no update: {reason}")),
            _ => {}
        });
    }
}

/// Whether this collector can be linked while it runs: it has an icon where a person does it, or it was installed with the installer (which opens the page
/// that links it). A collector with neither, run as a service on a machine with no screen, that is linked to nothing is wrongly set up: it says so and ends.
fn can_wait_to_be_linked(options: &Options) -> bool {
    (cfg!(windows) && !options.no_tray) || updater().installed()
}

/// Whether the folder of the collector holds no destination, and nothing was given on the command line or in the environment to send to.
fn nothing_linked(options: &Options) -> bool {
    let get = |name: &str| std::env::var(name).ok();
    let given = options.server.is_some()
        || options.key_file.is_some()
        || get("MNEMARR_SERVER").is_some()
        || get("MNEMARR_COLLECTOR_KEY").is_some();
    !given
        && config_dir(options.config_dir.as_deref(), &get)
            .is_some_and(|dir| destinations::load(&dir).is_empty())
}

fn collect(options: &Options, forever: bool) -> ExitCode {
    if let Some(path) = &options.log_file {
        let _ = LOG_FILE.set(PathBuf::from(path));
    }
    // A collector that was just installed is linked to nothing yet: it runs all the same, with its icon, which is where a person links it (a command that is
    // run once has nothing to do, and says so).
    let configs = if forever && nothing_linked(options) && can_wait_to_be_linked(options) {
        say(
            "this machine is not linked to any Mnemarr yet: link one from the icon, or with: mnemarr-collector link",
        );
        Vec::new()
    } else {
        match configurations(options) {
            Ok(configs) => configs,
            Err(code) => return code,
        }
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
    if let Some(first) = configs.first() {
        let probe = Collector::new(
            first.machine.clone(),
            first.projects.clone(),
            HttpUploader::new(&first.server, &first.key),
        );
        if !probe.masking_is_complete() {
            say(
                "a rule of the masking did not start: secrets are masked by the server alone until this is fixed",
            );
        }
    }
    for config in &configs {
        if config.server.starts_with("http://")
            && !mnemarr_collector::config::is_local(&config.server)
        {
            say(&format!(
                "warning: {} is plain http://: the key is sent without encryption",
                config.name
            ));
        }
        say(&format!(
            "{VERSION}: {} -> {} ({}) as {}",
            config.projects.display(),
            config.name,
            config.server,
            config.machine
        ));
    }
    let get = |name: &str| std::env::var(name).ok();
    let dir = config_dir(options.config_dir.as_deref(), &get);
    let condition = status::shared();
    if forever && !options.no_tray && cfg!(windows) {
        return collect_with_icon(options, configs, dir, condition);
    }
    collect_loop(options, configs, dir, forever, condition)
}

/// Where there is no icon to show (anywhere but Windows for now), it collects as it would without one.
#[cfg(not(windows))]
fn collect_with_icon(
    options: &Options,
    configs: Vec<Config>,
    dir: Option<PathBuf>,
    condition: status::Shared,
) -> ExitCode {
    collect_loop(options, configs, dir, true, condition)
}

/// Collects in a thread of its own and shows the icon in this one. When the icon cannot be made (a session with no taskbar), it goes on without it.
#[cfg(windows)]
fn collect_with_icon(
    options: &Options,
    configs: Vec<Config>,
    dir: Option<PathBuf>,
    condition: status::Shared,
) -> ExitCode {
    let shown_dir = dir.clone();
    let known: Vec<(String, String)> = configs
        .iter()
        .map(|config| (config.name.clone(), config.server.clone()))
        .collect();
    let shared = condition.clone();
    let worker_options = options.clone();
    let worker = thread::spawn(move || collect_loop(&worker_options, configs, dir, true, shared));
    if let Some(folder) = shown_dir {
        // The icon never returns: *Quit* ends the program from inside. It only gives back an error, when it could not be made.
        let Err(error) = mnemarr_collector::tray::run(folder, known, condition, updater().clone());
        say(&format!("no icon in the notification area: {error}"));
    }
    worker.join().unwrap_or(ExitCode::from(1))
}

/// The name this machine suggests for itself (or the one it was given), when it is one that Mnemarr accepts.
fn machine_name(given: Option<&String>) -> Result<String, ExitCode> {
    let get = |name: &str| std::env::var(name).ok();
    let read = |path: &str| std::fs::read_to_string(path).ok();
    let Some(machine) = given
        .cloned()
        .or_else(|| get("MNEMARR_MACHINE"))
        .or_else(|| default_machine(&get, &read))
    else {
        say("the name of this machine is needed: --machine or MNEMARR_MACHINE");
        return Err(ExitCode::from(2));
    };
    if !is_machine_name(&machine) {
        say(&format!(
            "'{machine}' is not a name that Mnemarr accepts for a machine: give another with --machine"
        ));
        return Err(ExitCode::from(2));
    }
    Ok(machine)
}

/// Keeps what Mnemarr gave: this machine now also sends to it, with its own key.
fn keep(
    dir: &std::path::Path,
    server: &str,
    enrolled: &enrol::Enrolled,
    insecure: bool,
    name: Option<&str>,
) -> ExitCode {
    match destinations::add(
        dir,
        server,
        &enrolled.machine,
        insecure,
        name,
        &enrolled.key,
    ) {
        Ok(destination) => {
            say(&format!(
                "linked to {} ({}) as {}. It sends there from its next pass: if it is not running, start it with: mnemarr-collector install",
                destination.name, destination.server, enrolled.machine
            ));
            ExitCode::SUCCESS
        }
        Err(error) => {
            say(&format!(
                "linked, but the key could not be kept in {}: {error}. Link again.",
                dir.display()
            ));
            ExitCode::from(1)
        }
    }
}

/// Links this machine to a Mnemarr, in addition to those it already sends to (ADR 0030). With a code a person made in Mnemarr it is exchanged for the key
/// (what `enroll` always did); without, the machine asks, shows the address where a person approves it, and waits.
fn link(options: &LinkOptions) -> ExitCode {
    let get = |name: &str| std::env::var(name).ok();
    let Some(server) = options.server.clone().or_else(|| get("MNEMARR_SERVER")) else {
        let _ = writeln!(io::stderr(), "link needs --server\n\n{}", usage());
        return ExitCode::from(2);
    };
    let server = server.trim_end_matches('/').to_string();
    if let Err(error) = check_server(&server, options.allow_insecure_http) {
        say(&error.to_string());
        return ExitCode::from(2);
    }
    let Some(dir) = config_dir(options.config_dir.as_deref(), &get) else {
        say("the place to keep the key cannot be found: give --config-dir");
        return ExitCode::from(2);
    };
    if let Some(code) = &options.code {
        // With a code, the name is the one the code was made for: it has to be a name that Mnemarr accepts.
        let machine = match machine_name(options.machine.as_ref()) {
            Ok(machine) => machine,
            Err(code) => return code,
        };
        return match enrol::exchange(&server, code, &machine) {
            Ok(enrolled) => keep(
                &dir,
                &server,
                &enrolled,
                options.allow_insecure_http,
                options.name.as_deref(),
            ),
            Err(error) => {
                say(&error.to_string());
                ExitCode::from(1)
            }
        };
    }
    // Asking, the machine only suggests what it is called, as it is: Mnemarr cleans it and the person who approves chooses the name.
    let read = |path: &str| std::fs::read_to_string(path).ok();
    let hostname = options
        .machine
        .clone()
        .or_else(|| get("MNEMARR_MACHINE"))
        .or_else(|| default_machine(&get, &read))
        .unwrap_or_else(|| "this computer".to_string());
    let show = |request: &enrol::PairingRequest| {
        say(&format!(
            "to link this computer, open {} and check that the code is {}",
            request.verification_url, request.user_code
        ));
        if !options.no_browser && !browser::is_same_server(&server, &request.verification_url) {
            say(
                "(the address is not on the Mnemarr you typed, so it was not opened: check it before you open it)",
            );
        } else if !options.no_browser && !browser::open(&request.verification_url) {
            say("(the browser could not be opened: open the address by hand)");
        }
        say("waiting for you to approve it in Mnemarr...");
    };
    match enrol::link_by_asking(&server, &hostname, &show, &thread::sleep) {
        Ok(LinkOutcome::Linked(enrolled)) => keep(
            &dir,
            &server,
            &enrolled,
            options.allow_insecure_http,
            options.name.as_deref(),
        ),
        Ok(LinkOutcome::Rejected) => {
            say("a person refused this computer in Mnemarr");
            ExitCode::from(1)
        }
        Ok(LinkOutcome::Expired) => {
            say("nobody answered in time: ask again");
            ExitCode::from(1)
        }
        Err(error) => {
            say(&error.to_string());
            ExitCode::from(1)
        }
    }
}

/// `enroll` is `link` with a code: it joins the machine to that Mnemarr, in addition to the others.
fn enroll(options: &EnrollOptions) -> ExitCode {
    if options.code.is_none() {
        let _ = writeln!(
            io::stderr(),
            "enroll needs --server and --code (or use: mnemarr-collector link)\n\n{}",
            usage()
        );
        return ExitCode::from(2);
    }
    link(&LinkOptions {
        server: options.server.clone(),
        code: options.code.clone(),
        name: None,
        machine: options.machine.clone(),
        config_dir: options.config_dir.clone(),
        allow_insecure_http: options.allow_insecure_http,
        no_browser: true,
    })
}

/// Stops sending to a Mnemarr. What it received stays there; to remove the machine for good, revoke it in that Mnemarr.
fn unlink(options: &UnlinkOptions) -> ExitCode {
    let get = |name: &str| std::env::var(name).ok();
    let Some(dir) = config_dir(options.config_dir.as_deref(), &get) else {
        say("the folder of the collector cannot be found: give --config-dir");
        return ExitCode::from(2);
    };
    let listed = destinations::load(&dir);
    let Some(name) = &options.name else {
        let names: Vec<&str> = listed
            .iter()
            .map(|destination| destination.name.as_str())
            .collect();
        say(&format!(
            "unlink needs the name of a Mnemarr; this machine sends to: {}",
            if names.is_empty() {
                "none".to_string()
            } else {
                names.join(", ")
            }
        ));
        return ExitCode::from(2);
    };
    match destinations::remove(&dir, name) {
        Ok(true) => {
            say(&format!(
                "no longer sends to {name}. What it received stays there; to remove this machine from {name} for good, revoke it in that Mnemarr"
            ));
            ExitCode::SUCCESS
        }
        Ok(false) => {
            say(&format!(
                "this machine does not send to a Mnemarr called {name} (see: mnemarr-collector destinations)"
            ));
            ExitCode::from(1)
        }
        Err(error) => {
            say(&format!("it could not be unlinked: {error}"));
            ExitCode::from(1)
        }
    }
}

/// Lists the Mnemarr this machine sends to.
fn list_destinations(options: &Options) -> ExitCode {
    let get = |name: &str| std::env::var(name).ok();
    let Some(dir) = config_dir(options.config_dir.as_deref(), &get) else {
        say("the folder of the collector cannot be found: give --config-dir");
        return ExitCode::from(2);
    };
    let listed = destinations::load(&dir);
    if listed.is_empty() {
        let _ = writeln!(
            io::stdout(),
            "this machine does not send to any Mnemarr yet (run: mnemarr-collector link)"
        );
    }
    for destination in listed {
        let note = match mnemarr_collector::config::check_destination(&dir, &destination) {
            Ok(_) => String::new(),
            Err(error) => format!("  (cannot be used: {})", error.0),
        };
        let _ = writeln!(
            io::stdout(),
            "{}  {}  as {}{note}",
            destination.name,
            destination.server,
            destination.machine
        );
    }
    ExitCode::SUCCESS
}

/// The folder of the collector and the program to run, for the commands that run it in the background. The machine has to be joined first.
fn background(options: &Options) -> Result<(std::path::PathBuf, std::path::PathBuf), ExitCode> {
    configurations(options)?;
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

/// What happens when the installed program is opened with nothing to say (the installer starts it so when it is done, the updater after an update): the
/// entry that starts it with the session is made right (it points at the program that an update does not change), and the collector starts in the
/// background. A person who clicks it again finds it running and nothing else happens.
fn launch_installed() -> ExitCode {
    let get = |name: &str| std::env::var(name).ok();
    let (Some(dir), Ok(running)) = (config_dir(None, &get), std::env::current_exe()) else {
        return ExitCode::from(1);
    };
    let exe = update::stable_exe(&running);
    if cfg!(windows) {
        let _ = service::register(&exe, None);
        if let Some(root) = exe.parent() {
            let _ = update::write_shim(root);
        }
    }
    match service::start(&dir, &exe, None) {
        Ok(Started::Started | Started::AlreadyRunning) => ExitCode::SUCCESS,
        _ => ExitCode::from(1),
    }
}

/// `update`: look for a newer version at the Mnemarr this machine sends to, and apply it; or, with `--auto`, say whether the collector applies updates itself.
fn update_command(options: &UpdateOptions) -> ExitCode {
    let get = |name: &str| std::env::var(name).ok();
    let Some(dir) = config_dir(options.config_dir.as_deref(), &get) else {
        say("the folder of the collector cannot be found: give --config-dir");
        return ExitCode::from(2);
    };
    if let Some(auto) = options.auto {
        return match update::save_settings(&dir, &update::Settings { auto }) {
            Ok(()) => {
                say(if auto {
                    "updates are applied by the collector itself, between two passes"
                } else {
                    "updates are downloaded and wait: apply them with `mnemarr-collector update` or from the icon"
                });
                ExitCode::SUCCESS
            }
            Err(error) => {
                say(&format!("the choice could not be kept: {error}"));
                ExitCode::from(1)
            }
        };
    }
    let updater = updater();
    if !updater.installed() {
        say(&UpdateState::NotInstalled.text());
        return ExitCode::from(1);
    }
    say(&format!(
        "version {}: asking the Mnemarr this machine sends to...",
        version_text()
    ));
    match updater.check(&destinations::load(&dir)) {
        UpdateState::Ready(version) if options.check_only => {
            say(&format!(
                "version {version} is downloaded: apply it with `mnemarr-collector update`"
            ));
            ExitCode::SUCCESS
        }
        UpdateState::Ready(version) => {
            say(&format!(
                "applying version {version}: the collector restarts"
            ));
            match updater.apply() {
                Ok(()) => ExitCode::SUCCESS,
                Err(error) => {
                    say(&format!("it could not be applied: {error}"));
                    ExitCode::from(1)
                }
            }
        }
        UpdateState::Failed(reason) => {
            say(&format!("no update: {reason}"));
            ExitCode::from(1)
        }
        _ => {
            say(&format!("up to date: version {}", version_text()));
            ExitCode::SUCCESS
        }
    }
}

/// Asks the collector to be quiet: for a while (`--minutes`), or until `resume`. It also stops the icon from showing "online": the running collector reads
/// the same file.
fn pause(options: &Options) -> ExitCode {
    let get = |name: &str| std::env::var(name).ok();
    let Some(dir) = config_dir(options.config_dir.as_deref(), &get) else {
        say("the folder of the collector cannot be found: give --config-dir");
        return ExitCode::from(2);
    };
    let result = match options.pause_minutes {
        Some(minutes) => control::pause_for(
            &dir,
            std::time::Duration::from_secs(minutes.saturating_mul(60)),
            std::time::SystemTime::now(),
        ),
        None => control::pause_forever(&dir),
    };
    match result {
        Ok(()) => {
            say(&match options.pause_minutes {
                Some(minutes) => format!("paused for {minutes} minute(s): it reads and sends nothing, and says so to Mnemarr. To stop the pause: mnemarr-collector resume"),
                None => "paused until you resume it: it reads and sends nothing, and says so to Mnemarr. To stop the pause: mnemarr-collector resume".to_string(),
            });
            ExitCode::SUCCESS
        }
        Err(error) => {
            say(&format!("it could not be paused: {error}"));
            ExitCode::from(1)
        }
    }
}

fn resume(options: &Options) -> ExitCode {
    let get = |name: &str| std::env::var(name).ok();
    let Some(dir) = config_dir(options.config_dir.as_deref(), &get) else {
        say("the folder of the collector cannot be found: give --config-dir");
        return ExitCode::from(2);
    };
    match control::resume(&dir) {
        Ok(()) => {
            say("resumed: it collects again at its next pass");
            ExitCode::SUCCESS
        }
        Err(error) => {
            say(&format!("it could not be resumed: {error}"));
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
    let joined = resolve_all(options, &get, &read).ok().unwrap_or_default();
    let state = service::status(&dir);
    let machines = if joined.is_empty() {
        "not joined (run: mnemarr-collector link)".to_string()
    } else {
        joined
            .iter()
            .map(|config| config.machine.as_str())
            .collect::<Vec<_>>()
            .join(", ")
    };
    let servers = if joined.is_empty() {
        "-".to_string()
    } else {
        joined
            .iter()
            .map(|config| format!("{} ({})", config.name, config.server))
            .collect::<Vec<_>>()
            .join(", ")
    };
    let _ = writeln!(
        io::stdout(),
        "machine: {machines}\nserver: {servers}\nversion: {}\nupdates: {}\nrunning: {}\nstarts with your session: {}\npaused: {}\nlog: {}",
        version_text(),
        updater().state().text(),
        state
            .running
            .map_or("no".to_string(), |pid| format!("yes (process {pid})")),
        if state.starts_with_session {
            "yes"
        } else {
            "no"
        },
        if control::current(&dir, std::time::SystemTime::now()).is_some() {
            "yes (mnemarr-collector resume)"
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
    // Started by the installer or the updater to do one of its tasks, it does it here and ends; otherwise this returns at once. The arguments of those tasks
    // are always plain text: one that is not is left to the usage error below (the updater reads them as text and would panic).
    if std::env::args_os().all(|argument| argument.to_str().is_some()) {
        update::startup();
    }
    // Arguments that are not valid UTF-8 are kept readable and end up as a usage error, never as a panic.
    let args: Vec<String> = std::env::args_os()
        .skip(1)
        .map(|argument| argument.to_string_lossy().into_owned())
        .collect();
    if args.is_empty() && updater().installed() {
        return launch_installed();
    }
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
        Ok(Command::Link(options)) => link(&options),
        Ok(Command::Unlink(options)) => unlink(&options),
        Ok(Command::Destinations(options)) => list_destinations(&options),
        Ok(Command::Install(options)) => install(&options),
        Ok(Command::Uninstall(options)) => uninstall(&options),
        Ok(Command::Start(options)) => start(&options),
        Ok(Command::Stop(options)) => stop(&options),
        Ok(Command::Status(options)) => status(&options),
        Ok(Command::Pause(options)) => pause(&options),
        Ok(Command::Resume(options)) => resume(&options),
        Ok(Command::Update(options)) => update_command(&options),
        Err(error) => {
            // If even standard error is closed there is nobody left to tell.
            let _ = writeln!(io::stderr(), "{error}\n\n{}", usage());
            ExitCode::from(2)
        }
    }
}
