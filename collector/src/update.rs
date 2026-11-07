//! Installing and updating through Velopack (ADR 0032).
//!
//! A collector that was installed with the installer of Mnemarr lives in its own folder (`%LocalAppData%\MnemarrCollector` on Windows), with a stable
//! program at the root that starts the current version, and can replace itself: it asks each Mnemarr it sends to for the newest version it carries, takes
//! the newest of them, downloads it in the background and applies it when it is quiet. A collector that was not installed that way (run by hand, from a
//! build, from a downloaded program that carries its setting) is not touched: `Updater::detect` says so, and nothing here does anything.
//!
//! What can be tried without Velopack is here as plain functions (which offer is the newest, which address may carry an update, what the settings say, what
//! the script that puts the program on the PATH says); what needs an installed program is a thin layer over the `velopack` crate.

use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};

use semver::Version;
use velopack::sources::HttpSource;
use velopack::{HttpOptions, UpdateCheck, UpdateInfo, UpdateManager, VelopackApp};

use crate::config::config_dir;
use crate::destinations::Destination;
#[cfg(windows)]
use crate::service;

/// The name of the package: the folder of the installed program is named after it.
pub const PACK_ID: &str = "MnemarrCollector";

/// The file in the folder of the collector that holds what the person chose about updates.
pub const SETTINGS_FILE: &str = "update.json";

/// How long a request to a Mnemarr for its feed or its package may take.
const TIMEOUT_MILLISECONDS: u64 = 60_000;

/// Where a Mnemarr serves its feed of updates (ADR 0032): `releases.<channel>.json` and the packages sit in this folder.
pub fn feed_url(server: &str) -> String {
    format!("{}/api/v1/ingest/updates", server.trim_end_matches('/'))
}

/// Whether a program may be taken from this address. An update is code that will run: it is taken over HTTPS, or from this very computer, and never over a plain
/// `http://` address on a network, even one that the person allowed for sending conversations.
pub fn may_carry_updates(server: &str) -> bool {
    let lower = server.trim().to_ascii_lowercase();
    if lower.starts_with("https://") {
        return true;
    }
    let Some(rest) = lower.strip_prefix("http://") else {
        return false;
    };
    let authority = rest.split(['/', '?', '#']).next().unwrap_or_default();
    // A user name before the host (`localhost:80@evil.example`) is how an address says one host and means another: it carries nothing.
    if authority.contains('@') {
        return false;
    }
    let host = authority
        .strip_prefix('[')
        .and_then(|inner| inner.split_once(']'))
        .map_or_else(
            || authority.split(':').next().unwrap_or_default(),
            |(inside, _)| inside,
        );
    matches!(host, "localhost" | "127.0.0.1" | "::1")
}

/// What one Mnemarr says is the newest version it carries (when it is newer than the one that is installed).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Offer {
    pub destination: String,
    pub version: Version,
}

/// The newest of the offers: the Mnemarr a collector takes its update from when it sends to several, which carry different builds (a development one and a
/// production one). When two carry the same version, the first one listed.
pub fn newest(offers: &[Offer]) -> Option<&Offer> {
    newest_index(offers).map(|index| &offers[index])
}

/// The place of the newest offer in the list (the first one when two carry the same version).
pub fn newest_index(offers: &[Offer]) -> Option<usize> {
    offers
        .iter()
        .enumerate()
        .fold(None, |best: Option<usize>, (index, offer)| match best {
            Some(found) if offers[found].version >= offer.version => Some(found),
            _ => Some(index),
        })
}

/// Where the collector is at, for the icon and for `status`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum UpdateState {
    /// Not installed with the installer: it updates by hand, and says nothing about it.
    NotInstalled,
    /// Installed, and nothing newer is known.
    UpToDate,
    /// Asking the Mnemarr it sends to.
    Checking,
    /// A newer version is being taken.
    Downloading(String),
    /// A newer version is here, and is applied when the collector restarts.
    Ready(String),
    /// The last attempt failed, with what went wrong.
    Failed(String),
}

impl UpdateState {
    /// A line for a person.
    pub fn text(&self) -> String {
        match self {
            Self::NotInstalled => {
                "not installed with the installer: it is not updated by itself".to_string()
            }
            Self::UpToDate => "up to date".to_string(),
            Self::Checking => "looking for an update".to_string(),
            Self::Downloading(version) => format!("taking version {version}"),
            Self::Ready(version) => {
                format!("version {version} is ready: it is applied when the collector restarts")
            }
            Self::Failed(reason) => format!("the last check failed: {reason}"),
        }
    }
}

/// What the person chose about updates.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Settings {
    /// An update that is ready is applied by the collector itself, between two passes. Off: it is applied when the person asks (the icon, `update`).
    pub auto: bool,
}

impl Default for Settings {
    fn default() -> Self {
        Self { auto: true }
    }
}

/// The settings of the folder, or the defaults when there are none or they cannot be read.
pub fn load_settings(dir: &Path) -> Settings {
    std::fs::read_to_string(dir.join(SETTINGS_FILE))
        .ok()
        .and_then(|text| serde_json::from_str::<serde_json::Value>(&text).ok())
        .and_then(|value| value.get("auto").and_then(serde_json::Value::as_bool))
        .map_or_else(Settings::default, |auto| Settings { auto })
}

/// Keeps the settings.
pub fn save_settings(dir: &Path, settings: &Settings) -> std::io::Result<()> {
    std::fs::create_dir_all(dir)?;
    let text = serde_json::json!({ "auto": settings.auto }).to_string();
    std::fs::write(dir.join(SETTINGS_FILE), text)
}

/// The name the installer gave the application (what the list of installed applications shows): the program at the root of its folder is named after it.
fn installed_title() -> Option<String> {
    velopack::locator::auto_locate_app_manifest(velopack::locator::LocationContext::FromCurrentExe)
        .ok()
        .map(|locator| locator.get_manifest_title())
}

/// The program that starts the installed collector, at a place that an update does not change: the program that runs is in `current`, which is replaced, and
/// the one at the root of the folder (named after the application) starts it. A program that is not in an installed folder is its own.
pub fn stable_exe(running: &Path) -> PathBuf {
    stable_exe_named(running, installed_title().as_deref())
}

/// `stable_exe`, given the name the installer gave the application: the program at the root is `<name>.exe`, or else the one that runs, by its own name.
pub fn stable_exe_named(running: &Path, title: Option<&str>) -> PathBuf {
    let Some(file) = running.file_name() else {
        return running.to_path_buf();
    };
    let installed_root = running
        .parent()
        .filter(|folder| folder.file_name().is_some_and(|name| name == "current"))
        .and_then(Path::parent)
        .filter(|root| {
            root.join(if cfg!(windows) {
                "Update.exe"
            } else {
                "UpdateNix"
            })
            .exists()
        });
    let Some(root) = installed_root else {
        return running.to_path_buf();
    };
    title
        .map(|title| root.join(format!("{title}.exe")))
        .filter(|stub| stub.exists())
        .unwrap_or_else(|| root.join(file))
}

/// The folder, as the scripts below read it: it is given to PowerShell as data (an environment variable), never written into the text of the script, so that a
/// name with a quote of any kind in it cannot end up as a command.
pub const FOLDER_VARIABLE: &str = "MNEMARR_FOLDER";

/// The script (PowerShell) that adds the folder named by `MNEMARR_FOLDER` to the PATH of the user, once, and tells Windows. Pure text, so that it is tried anywhere.
pub fn add_to_path_script() -> String {
    "$d = $env:MNEMARR_FOLDER; $p = [Environment]::GetEnvironmentVariable('Path', 'User'); \
$parts = @(($p -split ';') | Where-Object { $_ -and ($_ -ne $d) }); \
[Environment]::SetEnvironmentVariable('Path', (($parts + $d) -join ';'), 'User')"
        .to_string()
}

/// The script that takes the folder named by `MNEMARR_FOLDER` out of the PATH of the user.
pub fn remove_from_path_script() -> String {
    "$d = $env:MNEMARR_FOLDER; $p = [Environment]::GetEnvironmentVariable('Path', 'User'); \
$parts = @(($p -split ';') | Where-Object { $_ -and ($_ -ne $d) }); \
[Environment]::SetEnvironmentVariable('Path', ($parts -join ';'), 'User')"
        .to_string()
}

#[cfg(windows)]
fn run_script(script: &str, folder: &Path) {
    use std::os::windows::process::CommandExt;
    const CREATE_NO_WINDOW: u32 = 0x0800_0000;
    let root = std::env::var_os("SystemRoot").unwrap_or_else(|| "C:\\Windows".into());
    let _ = std::process::Command::new(
        Path::new(&root)
            .join("System32")
            .join("WindowsPowerShell")
            .join("v1.0")
            .join("powershell.exe"),
    )
    .args([
        "-NoProfile",
        "-NonInteractive",
        "-ExecutionPolicy",
        "Bypass",
        "-Command",
        script,
    ])
    .env(FOLDER_VARIABLE, folder)
    .creation_flags(CREATE_NO_WINDOW)
    .output();
}

/// What the installer asks of the program, in the thirty seconds it gives it (Windows): the program is made to start with the session of the user and to be
/// found by name in a terminal. It is not started here: the installer starts it when it is done.
#[cfg(windows)]
fn after_install() {
    let Ok(running) = std::env::current_exe() else {
        return;
    };
    let stable = stable_exe(&running);
    let _ = service::register(&stable, None);
    if let Some(root) = stable.parent() {
        run_script(&add_to_path_script(), root);
    }
}

/// Before the program is taken away: it stops, and no longer starts with the session or sits on the PATH. The machine stays joined: its keys and its
/// positions are in the folder of the collector, which is not the one that is removed (ADR 0032).
#[cfg(windows)]
fn before_uninstall() {
    let get = |name: &str| std::env::var(name).ok();
    if let Some(dir) = config_dir(None, &get) {
        let _ = service::uninstall(&dir);
    }
    if let Ok(running) = std::env::current_exe()
        && let Some(root) = stable_exe(&running).parent()
    {
        run_script(&remove_from_path_script(), root);
    }
}

/// Before a new version replaces this one: the running collector stops, so that its files can be replaced. The updater starts the new one.
#[cfg(windows)]
fn before_update() {
    let get = |name: &str| std::env::var(name).ok();
    if let Some(dir) = config_dir(None, &get) {
        service::stop(&dir);
    }
}

/// The first thing `main` does. When the program is started by the installer or the updater to do one of its tasks, this does it and ends the process; when
/// a new version was downloaded and waits, it is applied here; otherwise it returns and the program goes on as it always did.
pub fn startup() {
    let app = VelopackApp::build();
    #[cfg(windows)]
    let app = app
        .on_after_install_fast_callback(|_| after_install())
        .on_before_uninstall_fast_callback(|_| before_uninstall())
        .on_before_update_fast_callback(|_| before_update());
    let mut app = app;
    app.run();
}

/// An address at which this program may ask for updates, with the name of the destination it belongs to.
fn source_of(server: &str) -> HttpSource {
    HttpSource::new_with_options(
        feed_url(server),
        HttpOptions {
            Headers: Vec::new(),
            TimeoutMilliseconds: TIMEOUT_MILLISECONDS,
        },
    )
}

/// What was found by a check, and kept until it is applied.
struct Pending {
    manager: UpdateManager,
    info: UpdateInfo,
}

/// The collector's side of updates: whether it is installed, where it is at, and the update that is ready.
#[derive(Clone)]
pub struct Updater {
    version: Option<Version>,
    state: Arc<Mutex<UpdateState>>,
    pending: Arc<Mutex<Option<Arc<Pending>>>>,
}

impl Updater {
    /// Finds out whether this program was installed with the installer (and which version it is).
    pub fn detect() -> Self {
        let version = UpdateManager::new(HttpSource::new(""), None, None)
            .ok()
            .map(|manager| manager.get_current_version());
        let state = if version.is_some() {
            UpdateState::UpToDate
        } else {
            UpdateState::NotInstalled
        };
        Self {
            version,
            state: Arc::new(Mutex::new(state)),
            pending: Arc::new(Mutex::new(None)),
        }
    }

    /// Whether the program was installed with the installer, and can replace itself.
    pub fn installed(&self) -> bool {
        self.version.is_some()
    }

    /// The version that is installed, as the installer knows it.
    pub fn version(&self) -> Option<String> {
        self.version.as_ref().map(ToString::to_string)
    }

    pub fn state(&self) -> UpdateState {
        self.state
            .lock()
            .map_or(UpdateState::NotInstalled, |state| state.clone())
    }

    fn set(&self, state: UpdateState) {
        if let Ok(mut current) = self.state.lock() {
            *current = state;
        }
    }

    /// Whether an update is downloaded and waits to be applied.
    pub fn ready(&self) -> bool {
        matches!(self.state(), UpdateState::Ready(_))
    }

    /// Says that a check begins, unless one is under way or an update is already waiting: only one at a time, and the state is looked at and set together.
    fn begin(&self) -> bool {
        let Ok(mut state) = self.state.lock() else {
            return false;
        };
        if matches!(
            *state,
            UpdateState::Checking | UpdateState::Downloading(_) | UpdateState::Ready(_)
        ) {
            return false;
        }
        *state = UpdateState::Checking;
        true
    }

    /// Asks each Mnemarr of the list what it carries, and gives back what is newer than the installed version, with the last thing that went wrong.
    fn look(destinations: &[&Destination]) -> (Vec<(Offer, Pending)>, Option<String>) {
        let mut found = Vec::new();
        let mut failure = None;
        for destination in destinations {
            let manager = match UpdateManager::new(source_of(&destination.server), None, None) {
                Ok(manager) => manager,
                Err(error) => {
                    failure = Some(error.to_string());
                    continue;
                }
            };
            match manager.check_for_updates() {
                Ok(UpdateCheck::UpdateAvailable(info)) => {
                    if let Ok(version) = Version::parse(&info.TargetFullRelease.Version) {
                        let offer = Offer {
                            destination: destination.name.clone(),
                            version,
                        };
                        found.push((
                            offer,
                            Pending {
                                manager,
                                info: *info,
                            },
                        ));
                    }
                }
                Ok(_) => {}
                Err(error) => failure = Some(format!("{}: {error}", destination.name)),
            }
        }
        (found, failure)
    }

    /// Takes the version of the winning offer and keeps it, ready to be applied.
    fn take(&self, offer: &Offer, pending: Pending) -> UpdateState {
        self.set(UpdateState::Downloading(offer.version.to_string()));
        if let Err(error) = pending.manager.download_updates(&pending.info, None) {
            return UpdateState::Failed(format!("{}: {error}", offer.destination));
        }
        if let Ok(mut slot) = self.pending.lock() {
            *slot = Some(Arc::new(pending));
        }
        UpdateState::Ready(offer.version.to_string())
    }

    /// Asks each Mnemarr in the list what it carries, takes the newest version of those that are newer than the installed one, and downloads it. Blocks for as
    /// long as that takes: it is run apart from the passes. A Mnemarr that cannot be reached or whose address may not carry updates is passed over; when
    /// none may, it says so. Nothing is done again while an update waits.
    pub fn check(&self, destinations: &[Destination]) -> UpdateState {
        if !self.installed() {
            return UpdateState::NotInstalled;
        }
        let allowed: Vec<&Destination> = destinations
            .iter()
            .filter(|destination| may_carry_updates(&destination.server))
            .collect();
        if allowed.is_empty() {
            // Said, not skipped in silence: a person who linked this computer to a Mnemarr over plain http wonders why nothing ever updates.
            let state = UpdateState::Failed(if destinations.is_empty() {
                "it is not linked to any Mnemarr".to_string()
            } else {
                "none of the Mnemarr it sends to is reached over HTTPS (an update is code: it is never taken over plain http)"
                    .to_string()
            });
            self.set(state.clone());
            return state;
        }
        if !self.begin() {
            return self.state();
        }
        let (mut found, failure) = Self::look(&allowed);
        let offers: Vec<Offer> = found.iter().map(|(offer, _)| offer.clone()).collect();
        let state = match newest_index(&offers) {
            Some(index) => {
                let (offer, pending) = found.swap_remove(index);
                self.take(&offer, pending)
            }
            None => failure.map_or(UpdateState::UpToDate, UpdateState::Failed),
        };
        self.set(state.clone());
        state
    }

    /// The reason the last check failed, short enough to be said to a Mnemarr; nothing when it did not.
    pub fn note(&self) -> Option<String> {
        match self.state() {
            UpdateState::Failed(reason) => Some(reason.chars().take(120).collect()),
            _ => None,
        }
    }

    /// Applies the update that is ready, and restarts the collector: the process ends and the updater starts the new version. Positions are kept after each
    /// pass, so nothing is lost wherever this is called from. An error when there is none ready, or when the updater could not be started.
    pub fn apply(&self) -> Result<(), String> {
        let pending = self
            .pending
            .lock()
            .map_err(|_| "the update cannot be read".to_string())?
            .clone()
            .ok_or_else(|| "no update is ready".to_string())?;
        pending
            .manager
            .apply_updates_and_restart(&pending.info)
            .map_err(|error| error.to_string())
    }
}

/// Where the settings and the keys are kept, for the commands that have no `--config-dir` of their own.
pub fn default_config_dir() -> Option<PathBuf> {
    let get = |name: &str| std::env::var(name).ok();
    config_dir(None, &get)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn offer(destination: &str, version: &str) -> Offer {
        Offer {
            destination: destination.to_string(),
            version: Version::parse(version).unwrap(),
        }
    }

    #[test]
    fn should_take_the_update_from_the_mnemarr_that_carries_the_newest_version() {
        let offers = [
            offer("dev", "0.3.0-dev.12"),
            offer("prod", "0.2.0"),
            offer("other", "0.3.0-dev.9"),
        ];

        assert_eq!(newest(&offers).unwrap().destination, "dev");
        assert!(newest(&[]).is_none());
    }

    #[test]
    fn should_order_a_release_after_its_pre_releases_and_numbers_after_numbers() {
        let offers = [
            offer("a", "1.0.0-dev.9"),
            offer("b", "1.0.0"),
            offer("c", "1.0.0-dev.10"),
        ];

        assert_eq!(newest(&offers).unwrap().destination, "b");
        assert_eq!(
            newest(&[offer("a", "1.0.0-dev.9"), offer("c", "1.0.0-dev.10")])
                .unwrap()
                .destination,
            "c",
            "dev.10 is newer than dev.9: numbers are compared as numbers"
        );
    }

    #[test]
    fn should_take_the_first_listed_when_two_carry_the_same_version() {
        let offers = [offer("first", "1.2.3"), offer("second", "1.2.3")];

        assert_eq!(newest(&offers).unwrap().destination, "first");
    }

    #[test]
    fn should_build_the_address_of_the_feed_of_a_mnemarr() {
        assert_eq!(
            feed_url("https://mnemarr.example/"),
            "https://mnemarr.example/api/v1/ingest/updates"
        );
        assert_eq!(
            feed_url("http://127.0.0.1:8383"),
            "http://127.0.0.1:8383/api/v1/ingest/updates"
        );
    }

    #[test]
    fn should_take_a_program_only_over_https_or_from_this_computer() {
        assert!(may_carry_updates("https://mnemarr.example"));
        assert!(may_carry_updates("HTTPS://Mnemarr.Example/path"));
        assert!(may_carry_updates("http://localhost:8383"));
        assert!(may_carry_updates("http://127.0.0.1:8383/"));
        assert!(may_carry_updates("http://[::1]:8383"));
        assert!(!may_carry_updates("http://10.0.0.10:8384"));
        assert!(!may_carry_updates("http://mnemarr.example"));
        assert!(!may_carry_updates("http://localhost.evil.example"));
        assert!(!may_carry_updates("ftp://mnemarr.example"));
        assert!(!may_carry_updates("mnemarr.example"));
    }

    #[test]
    fn should_say_in_words_where_an_update_is_at() {
        assert!(UpdateState::NotInstalled.text().contains("not installed"));
        assert!(
            UpdateState::Ready("1.2.3".to_string())
                .text()
                .contains("1.2.3")
        );
        assert!(
            UpdateState::Failed("down".to_string())
                .text()
                .contains("down")
        );
    }

    #[test]
    fn should_update_by_itself_unless_the_person_said_otherwise_and_keep_what_they_said() {
        let dir = std::env::temp_dir().join(format!("mnemarr-update-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);

        assert!(load_settings(&dir).auto, "no file: the default");
        save_settings(&dir, &Settings { auto: false }).unwrap();
        assert!(!load_settings(&dir).auto);
        std::fs::write(dir.join(SETTINGS_FILE), "{ not json").unwrap();
        assert!(
            load_settings(&dir).auto,
            "a file that cannot be read: the default"
        );
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn should_find_the_program_at_the_root_of_an_installed_folder_named_after_the_application_and_leave_any_other_alone()
     {
        let root = std::env::temp_dir().join(format!("mnemarr-root-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&root);
        std::fs::create_dir_all(root.join("current")).unwrap();
        let updater = root.join(if cfg!(windows) {
            "Update.exe"
        } else {
            "UpdateNix"
        });
        std::fs::write(&updater, "x").unwrap();
        let running = root.join("current").join("mnemarr-collector.exe");

        assert_eq!(
            stable_exe_named(&running, Some("Mnemarr collector")),
            root.join("mnemarr-collector.exe"),
            "no program of that name at the root: the running one's own name"
        );
        std::fs::write(root.join("Mnemarr collector.exe"), "stub").unwrap();
        assert_eq!(
            stable_exe_named(&running, Some("Mnemarr collector")),
            root.join("Mnemarr collector.exe"),
            "the program at the root is named after the application"
        );

        let elsewhere = std::env::temp_dir()
            .join("current")
            .join("mnemarr-collector.exe");
        assert_eq!(
            stable_exe_named(&elsewhere, Some("x")),
            elsewhere,
            "no updater beside it: it is not installed"
        );
        assert_eq!(
            stable_exe_named(Path::new("mnemarr-collector"), None),
            PathBuf::from("mnemarr-collector")
        );
        let _ = std::fs::remove_dir_all(&root);
    }

    #[test]
    fn should_write_scripts_that_add_a_folder_to_the_path_once_and_take_it_away_without_the_folder_in_their_text()
     {
        let add = add_to_path_script();
        let remove = remove_from_path_script();

        assert!(add.contains("$env:MNEMARR_FOLDER"));
        assert!(
            add.contains("'Path', 'User'"),
            "the PATH of the user, never the machine's"
        );
        assert!(
            add.contains("-ne $d") && add.contains("($parts + $d)"),
            "once: it is taken out before it is added"
        );
        assert!(remove.contains("($parts -join ';')") && !remove.contains("+ $d"));
        assert!(
            !add.contains('\\') && !remove.contains('\\'),
            "no folder is written in them: the name of a folder cannot become a command"
        );
    }

    #[test]
    fn should_take_a_program_only_from_the_computer_itself_and_not_from_an_address_that_hides_its_host()
     {
        assert!(!may_carry_updates("http://localhost:80@evil.example/"));
        assert!(!may_carry_updates("http://127.0.0.1@evil.example"));
        assert!(!may_carry_updates("http://user:pass@localhost:8383"));
    }
}
